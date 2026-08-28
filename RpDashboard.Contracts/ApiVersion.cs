namespace RpDashboard.Contracts;

/// <summary>
/// The plugin contract's breaking-change counter.
///
/// The whole point of the plugin split is that a tool can be rebuilt and dropped in
/// without rebuilding the host. That only holds while both sides agree on the shape of
/// <see cref="IToolHost"/> and friends, so every plugin stamps the version it was built
/// against and the host refuses to load a mismatch rather than failing later with a
/// MissingMethodException from somewhere confusing.
///
/// Adding a new member to <see cref="IToolHost"/> does not need a bump: an old plugin
/// never calls it. Changing or removing an existing member does.
/// </summary>
public static class HostApi
{
    public const int Version = 1;
}

/// <summary>
/// Marks an assembly as containing RP Dashboard tools, and records the contract version
/// it was compiled against. The host only reflects over assemblies carrying this, which
/// also keeps it from probing every stray DLL that lands in the plugins folder.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class RpToolAssemblyAttribute : Attribute
{
    public RpToolAssemblyAttribute(int apiVersion) => ApiVersion = apiVersion;

    public int ApiVersion { get; }
}
