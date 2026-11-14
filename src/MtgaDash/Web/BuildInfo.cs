using System.Reflection;

namespace MtgaDash.Web;

/// <summary>Version and build time, both stamped by MtgaDash.csproj at build time and only read here.</summary>
public static class BuildInfo
{
    public const string AppName = "MtgaDash";

    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>Already formatted as <c>yyyy-MM-dd HH:mm UTC</c>; empty when the build did not stamp it.</summary>
    public static string BuildTime { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTimeUtc")?.Value
        ?? "";
}
