using System.Reflection;

namespace ModernScreenShot.App.Services;

/// <summary>Display version of the running build, from AssemblyInformationalVersion. CI stamps the
/// full SemVer there (e.g. "0.0.0-dev.12+gc795cdc", see Agent.md / the CI dev-draft job); local
/// builds get the SDK-generated "1.0.0+<full sha>", whose long metadata is trimmed to 7 chars for
/// display. The numeric assembly version is the last-resort fallback.</summary>
internal static class AppVersion
{
    public static string Display { get; } = Shorten(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3)
        ?? "?");

    private static string Shorten(string version)
    {
        int plus = version.IndexOf('+');
        if (plus < 0) return version;
        var metadata = version[(plus + 1)..];
        // CI builds carry "g<7-char sha>" (8 chars); the SDK's auto-generated local metadata is a
        // full 40-char sha — trim it to the same width for display.
        if (metadata.Length > 8) metadata = metadata[..8];
        return version[..(plus + 1)] + metadata;
    }
}
