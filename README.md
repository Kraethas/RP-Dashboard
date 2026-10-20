# RP Dashboard

One window, one connection to the game, for the GTA World RP tools.

**Unit Alert**, **RP Pace** and **Street Medic** each used to run as their own process, and each
held its own WebSocket to FiveM's DevTools port, evaluating its own
`querySelectorAll('.chat__messages > li')` against the HUD every 500 ms. Three processes, three
sockets, six DOM scrapes a second inside the game, none of them aware of the others.

This hosts them as tabs and does that **once** — and only while a tool is actually listening.
Switch every tab off and the app stops touching the game entirely.

## Download and run

Grab one zip from the [latest release](https://github.com/Kraethas/RP-Dashboard/releases/latest),
unzip it anywhere, and run **`RpDashboard.exe`**. All four tabs are already inside — nothing else
to install or wire up.

| Download | Size | Needs |
|---|---|---|
| `RP-Dashboard-<version>-self-contained.zip` | ~63 MB zipped | nothing — just run it |
| `RP-Dashboard-<version>-framework-dependent.zip` | ~0.3 MB zipped | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) (x64) installed once |

Pick **self-contained** if you just want it to run. Pick **framework-dependent** if you already
have the .NET 8 Desktop Runtime (or don't mind installing it once) and want a tiny download.

The app lives in the system tray. First launch opens the window; closing it minimises to the
tray, and right-clicking the tray icon exits. Chat logs are written under
`%LOCALAPPDATA%\RpDashboard\ChatLogs` by default.

**The LogPusher tab is the exception** — it's a control panel for a separate Python program
(session-archiving to Notion) that needs its own API keys, so it won't do anything without that
setup. The other three tabs — Unit Alert, RP Pace, Street Medic — work the moment you launch.

## Tabs

| Tool | What it does | Lives in |
|---|---|---|
| Unit Alert | Plays a sound when a callsign appears in chat | `tools/UnitAlert` |
| RP Pace | Time since your last RP action, plus an emote library | [rp-pace](https://github.com/Kraethas/rp-pace) |
| Street Medic | Triage and first-aid protocol card | [StreetMedic](https://github.com/Kraethas/StreetMedic) |
| LogPusher | Start/stop and watch the session archiver | `tools/LogPusher` |

Each tool is a DLL in the `Plugins` folder beside the exe. **Rebuild a tool, drop the DLL in,
restart — the dashboard is not rebuilt.** Most of them live in their own repositories and keep
their own release cadence; the dashboard only needs rebuilding when the contract itself changes.

LogPusher is the odd one out: it is a Python program that reads chat *log files* off disk and
never touches the game, so its tab is a control panel over the existing exe rather than a port.

## Read-only, always

No tool here ever sends input to the game. They read chat and write to the clipboard; the human
pastes. That is what keeps them the right side of the server's rules, and there is deliberately
no method on the shared chat feed that could write.

## Building

```
dotnet build
dotnet run --project RpDashboard.Host
dotnet run --project tests/RpDashboard.SelfTest          # unit tests
dotnet run --project tests/RpDashboard.SelfTest -- --live # read the running game once
```

Packaging, into `dist/`:

```
.\build.ps1              # both flavours
.\build.ps1 -Zip         # and a zip for each
```

| | exe | folder | needs |
|---|---|---|---|
| `dist\self-contained\` | 146 MB | 155 MB | nothing |
| `dist\framework-dependent\` | 0.2 MB | 0.8 MB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime), x64 |

## More

`CLAUDE.md` carries the architecture, the invariants, the fragile spots, and the steps for
converting another tool into a tab.

When a tab is missing, read `%LOCALAPPDATA%\RpDashboard\last-start.log`.
