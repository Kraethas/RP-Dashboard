using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using RpDashboard.Contracts;

namespace RpDashboard.Host.Hosting;

/// <summary>
/// Finds the tools in the <c>Plugins</c> folder and instantiates them.
///
/// This is the mechanism behind the whole point of the split: a tool is rebuilt in its own
/// repo, its DLL is dropped in beside the others, and the dashboard picks it up on next start
/// without being rebuilt itself. The host only needs rebuilding when
/// <see cref="RpDashboard.Contracts"/> changes shape, which is why that assembly is kept as
/// small as it is.
///
/// Everything here is defensive on purpose. A plugin folder is a place where a stale, broken
/// or half-copied DLL will eventually appear, and when it does the right outcome is three
/// working tabs and one clear error — never a dashboard that won't start.
/// </summary>
internal static class PluginLoader
{
    public static PluginScanResult Load(string pluginsDirectory)
    {
        var tools = new List<LoadedTool>();
        var problems = new List<PluginProblem>();

        if (!Directory.Exists(pluginsDirectory))
            return new PluginScanResult(tools, problems);

        foreach (string path in Directory.EnumerateFiles(pluginsDirectory, "*.dll", SearchOption.AllDirectories))
        {
            try
            {
                LoadFrom(path, tools, problems);
            }
            catch (Exception ex)
            {
                problems.Add(new PluginProblem(Path.GetFileName(path), Describe(ex)));
            }
        }

        return new PluginScanResult(tools, problems);
    }

    private static void LoadFrom(string path, List<LoadedTool> tools, List<PluginProblem> problems)
    {
        string fileName = Path.GetFileName(path);

        var context = new PluginLoadContext(path);
        Assembly assembly = context.LoadFromAssemblyPath(path);

        var marker = assembly.GetCustomAttribute<RpToolAssemblyAttribute>();
        if (marker is null)
        {
            // Not a tool — almost certainly a dependency a tool shipped alongside itself.
            // Silent by design: reporting every support DLL as a problem would bury the real ones.
            return;
        }

        if (marker.ApiVersion != HostApi.Version)
        {
            problems.Add(new PluginProblem(
                fileName,
                $"Built against dashboard API v{marker.ApiVersion}, this dashboard is v{HostApi.Version}. " +
                "Rebuild the tool against the current contracts, or use the matching dashboard version."));
            return;
        }

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // A tool referencing something that isn't there still yields the types that did
            // load, so take those rather than losing the whole assembly over one bad type.
            types = ex.Types.Where(t => t is not null).ToArray()!;
            problems.Add(new PluginProblem(fileName, "Some types failed to load: " + Describe(ex)));
        }

        foreach (Type type in types)
        {
            if (!typeof(IRpTool).IsAssignableFrom(type)) continue;
            if (type.IsAbstract || type.IsInterface) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                problems.Add(new PluginProblem(
                    fileName,
                    $"{type.Name} implements IRpTool but has no public parameterless constructor."));
                continue;
            }

            try
            {
                var tool = (IRpTool)Activator.CreateInstance(type)!;
                tools.Add(new LoadedTool(tool, fileName, assembly.GetName().Version));
            }
            catch (Exception ex)
            {
                problems.Add(new PluginProblem(fileName, $"{type.Name} could not be created: {Describe(ex)}"));
            }
        }
    }

    /// <summary>
    /// A constructor that throws arrives wrapped, and the wrapper's own message says nothing
    /// useful — the inner one is the reason the tool didn't load, so that's what gets reported.
    /// </summary>
    private static string Describe(Exception ex)
        => ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;

    /// <summary>
    /// Gives each plugin its own load context so two tools can ship different versions of the
    /// same helper library without one of them losing.
    ///
    /// The exception is the contract assembly. It must resolve to the host's copy rather than a
    /// copy loaded here, or the <c>IRpTool</c> the plugin implements is a different type from
    /// the <c>IRpTool</c> the host casts to, and the load fails with an InvalidCastException
    /// between two types that print with the same name — the classic and thoroughly confusing
    /// version of this bug. Returning null from Load defers to the default context, which is
    /// what unifies them.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath)
            : base(name: Path.GetFileNameWithoutExtension(pluginPath), isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null) return null;

            // Shared with the host: the contract itself, and anything already loaded that the
            // two sides pass instances of. Plugin-local copies of these would break type identity.
            if (assemblyName.Name.StartsWith("RpDashboard.Contracts", StringComparison.OrdinalIgnoreCase))
                return null;

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

internal sealed record LoadedTool(IRpTool Tool, string FileName, Version? AssemblyVersion);

internal sealed record PluginProblem(string FileName, string Message);

internal sealed record PluginScanResult(
    IReadOnlyList<LoadedTool> Tools,
    IReadOnlyList<PluginProblem> Problems);
