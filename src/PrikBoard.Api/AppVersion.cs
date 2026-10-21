using System.Reflection;

namespace PrikBoard.Api;

/// <summary>The PrikBoard version this server runs, as set at build time (-p:Version, e.g. 0.0.27+abc1234).</summary>
public static class AppVersion
{
    public static readonly string Current =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>A version as people say it: without the build metadata after a "+" (the commit).</summary>
    public static string Short(string version) => version.Split('+')[0];
}
