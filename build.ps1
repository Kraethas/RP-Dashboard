<#
.SYNOPSIS
    Builds distributable RP Dashboard packages into dist\.

.DESCRIPTION
    Publishes the host, builds every tool, and assembles them:

        dist\self-contained\        RpDashboard.exe + Plugins\   -- runs on a bare machine
        dist\framework-dependent\   RpDashboard.exe + Plugins\   -- needs .NET 8 Desktop Runtime

    Both flavours are built by default, from one build of the tools. Same folder layout, same
    plugins, same behaviour — they differ only in whether the .NET runtime travels with them.

    Street Medic and RP Pace live in their own repositories, which is the whole point of the
    plugin split. This script picks them up if they are sitting beside this one and says so
    plainly if they aren't: a dashboard with two tabs is a valid build, not a failure.

.PARAMETER Flavour
    Which packages to produce: Both (default), SelfContained, or FrameworkDependent.

.PARAMETER SingleFile
    Bundle the app into one RpDashboard.exe (default). The Plugins folder still sits beside it —
    tools are loaded from disk by path, which is exactly what lets one be replaced without
    rebuilding the host, so they are deliberately not bundled in.

.PARAMETER Zip
    Also produce dist\RP-Dashboard-<version>-<flavour>.zip for each package built.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Flavour FrameworkDependent
    .\build.ps1 -Zip
#>

[CmdletBinding()]
param(
    [ValidateSet('Both', 'SelfContained', 'FrameworkDependent')]
    [string] $Flavour = 'Both',

    [switch] $SingleFile = $true,
    [switch] $Zip
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$configuration = 'Release'
$framework = 'net8.0-windows'
$runtime = 'win-x64'
$version = '1.0.0'

$outputRoot = Join-Path $root 'dist'

# Each tool: the project to build, the DLL it produces, and any content that has to travel with
# it. The content folders are the reason a plain "copy the DLL" isn't enough — both Street Medic
# and RP Pace keep their content editable beside the assembly rather than only baked into it.
$tools = @(
    @{ Name = 'Unit Alert'
       Project = Join-Path $root 'tools\UnitAlert\UnitAlert.Plugin.csproj'
       Assembly = 'UnitAlert.dll'
       Content = @() }

    @{ Name = 'LogPusher panel'
       Project = Join-Path $root 'tools\LogPusher\LogPusher.Plugin.csproj'
       Assembly = 'LogPusherPanel.dll'
       Content = @() }

    @{ Name = 'Street Medic'
       Project = Join-Path $root '..\StreetMedic\src\StreetMedic.Plugin\StreetMedic.Plugin.csproj'
       Assembly = 'StreetMedic.dll'
       Content = @('Assets') }

    @{ Name = 'RP Pace'
       Project = Join-Path $root '..\RP Pace\RPPace.Plugin\RPPace.Plugin.csproj'
       Assembly = 'RPPace.dll'
       Content = @('Emotes') }
)

$packages = @()
if ($Flavour -in 'Both', 'SelfContained')      { $packages += @{ Name = 'self-contained';      SelfContained = $true } }
if ($Flavour -in 'Both', 'FrameworkDependent') { $packages += @{ Name = 'framework-dependent'; SelfContained = $false } }

function Write-Step([string] $text) {
    Write-Host ''
    Write-Host $text -ForegroundColor Cyan
}

function Get-FolderSize([string] $path) {
    (Get-ChildItem $path -Recurse -File | Measure-Object -Property Length -Sum).Sum
}

function Clear-Output([string] $path) {
    # A dashboard running out of the last build holds its own folder open, and the raw
    # IOException from Remove-Item doesn't say which process or why. Common enough while
    # iterating to be worth naming.
    $holding = Get-Process -Name 'RpDashboard' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($path, [StringComparison]::OrdinalIgnoreCase) }

    if ($holding) {
        throw "RP Dashboard is running from $path (pid $($holding.Id -join ', ')). Close it and re-run."
    }

    if (-not (Test-Path $path)) { return }

    # Windows can hold a directory handle briefly after the process owning it exits, so one
    # retry turns a spurious failure into a pause instead of a stack trace.
    try {
        Remove-Item $path -Recurse -Force
    }
    catch [System.IO.IOException] {
        Start-Sleep -Milliseconds 800
        Remove-Item $path -Recurse -Force
    }
}

# ---------------------------------------------------------------- tools, once

Write-Step 'Building the tools'

$built = @()
$missing = @()

foreach ($tool in $tools) {
    if (-not (Test-Path $tool.Project)) {
        $missing += $tool.Name
        Write-Host "  skipped  $($tool.Name) — not found at $($tool.Project)" -ForegroundColor DarkYellow
        continue
    }

    & dotnet build $tool.Project -c $configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Building $($tool.Name) failed." }

    $toolBin = Join-Path (Split-Path $tool.Project -Parent) "bin\$configuration\$framework"
    $assembly = Join-Path $toolBin $tool.Assembly

    if (-not (Test-Path $assembly)) { throw "$($tool.Name) built but $($tool.Assembly) is missing." }

    foreach ($folder in $tool.Content) {
        if (-not (Test-Path (Join-Path $toolBin $folder))) {
            throw "$($tool.Name) is missing its $folder content."
        }
    }

    Write-Host "  $($tool.Name)" -ForegroundColor Green
    $built += @{ Name = $tool.Name; Bin = $toolBin; Assembly = $tool.Assembly; Content = $tool.Content }
}

if ($built.Count -eq 0) { throw "No tools built. A dashboard with no tabs isn't worth packaging." }

# ---------------------------------------------------------------- packages

$results = @()

foreach ($package in $packages) {
    $output = Join-Path $outputRoot $package.Name
    $pluginsOut = Join-Path $output 'Plugins'

    Write-Step "Packaging $($package.Name)"

    Clear-Output $output

    $publishArgs = @(
        'publish', (Join-Path $root 'RpDashboard.Host\RpDashboard.Host.csproj')
        '-c', $configuration
        '-r', $runtime
        '--self-contained', $(if ($package.SelfContained) { 'true' } else { 'false' })
        "-p:PublishSingleFile=$(if ($SingleFile) { 'true' } else { 'false' })"
        '-o', $output
        '--nologo'
        '-v', 'quiet'
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Publishing the host ($($package.Name)) failed." }

    # Debug symbols are for this machine, not for whoever is handed the zip.
    Get-ChildItem $output -Filter '*.pdb' -File | Remove-Item -Force

    New-Item -ItemType Directory -Path $pluginsOut -Force | Out-Null

    foreach ($tool in $built) {
        Copy-Item (Join-Path $tool.Bin $tool.Assembly) $pluginsOut -Force

        foreach ($folder in $tool.Content) {
            Copy-Item (Join-Path $tool.Bin $folder) (Join-Path $pluginsOut $folder) -Recurse -Force
        }
    }

    # The contract assembly the plugins bind to must be reachable from the host — loose in the
    # app folder, or inside the bundle when publishing single-file — and must NOT also appear in
    # Plugins\. A second copy there gives each plugin a different IRpTool type from the one the
    # host casts to, and every tool fails to load with a cast error between two identically-named
    # types. The plugin projects reference it with Private=false precisely to prevent that; this
    # is the check that the packaging didn't reintroduce it.
    if (Test-Path (Join-Path $pluginsOut 'RpDashboard.Contracts.dll')) {
        throw "RpDashboard.Contracts.dll ended up in Plugins\. Every tool would fail to load."
    }

    if (-not $SingleFile -and -not (Test-Path (Join-Path $output 'RpDashboard.Contracts.dll'))) {
        throw "RpDashboard.Contracts.dll is missing from the publish output."
    }

    $results += @{
        Name = $package.Name
        Path = $output
        Size = Get-FolderSize $output
        Exe = (Get-Item (Join-Path $output 'RpDashboard.exe')).Length
        SelfContained = $package.SelfContained
    }
}

# ---------------------------------------------------------------- zip

if ($Zip) {
    Write-Step 'Zipping'

    foreach ($result in $results) {
        $archive = Join-Path $outputRoot "RP-Dashboard-$version-$($result.Name).zip"
        if (Test-Path $archive) { Remove-Item $archive -Force }

        Compress-Archive -Path $result.Path -DestinationPath $archive
        $result.Zip = (Get-Item $archive).Length

        Write-Host ("  {0} ({1:N0} MB)" -f (Split-Path $archive -Leaf), ($result.Zip / 1MB))
    }
}

# ---------------------------------------------------------------- report

Write-Step 'Built'

Write-Host ("  {0} tool(s): {1}" -f $built.Count, (($built | ForEach-Object { $_.Name }) -join ', '))

if ($missing.Count -gt 0) {
    Write-Host "  Not included: $($missing -join ', ')" -ForegroundColor Yellow
    Write-Host "  Those tools live in their own repositories. Clone them beside this one and re-run." -ForegroundColor DarkGray
}

Write-Host ''

foreach ($result in $results) {
    Write-Host ("  {0}" -f $result.Path) -ForegroundColor White
    Write-Host ("    exe {0,7:N1} MB    total {1,7:N1} MB{2}" -f
        ($result.Exe / 1MB),
        ($result.Size / 1MB),
        $(if ($result.Zip) { "    zip {0,6:N1} MB" -f ($result.Zip / 1MB) } else { '' }))

    if (-not $result.SelfContained) {
        Write-Host '    Needs the .NET 8 Desktop Runtime:' -ForegroundColor DarkGray
        Write-Host '    https://dotnet.microsoft.com/download/dotnet/8.0/runtime  (Desktop Runtime, x64)' -ForegroundColor DarkGray
    }

    Write-Host ''
}
