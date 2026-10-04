using System.IO;
using ModernScreenShot.Core.Settings;

namespace ModernScreenShot.Core.Translation;

/// <summary>
/// Where the translation feature keeps its files.
///
/// The <b>runtime</b> (llama.cpp, MIT) ships next to the executable and is read-only at runtime;
/// the <b>model</b> (Hy-MT2-1.8B, Apache-2.0) is downloaded on demand into %LOCALAPPDATA% — the
/// same split the OCR feature uses for its bundled and downloaded models.
///
/// This replaced an Argos Translate engine that needed a 171 MB bundled CPython plus a JSON bridge
/// process; llama.cpp is a 42 MB native runtime and applies the model's own chat template, so the
/// translation quality went up while the shipped package got smaller.
/// </summary>
public static class TranslationEngine
{
    /// <summary>Sub-folder of the application directory that holds the llama.cpp runtime.</summary>
    public const string RuntimeFolderName = "llama";

    public static string RuntimeDir => Path.Combine(AppContext.BaseDirectory, RuntimeFolderName);

    public static string ServerExe => Path.Combine(RuntimeDir, "llama-server.exe");

    /// <summary>Version stamp written by tools/build-llama-runtime.ps1, for the settings page.</summary>
    public static string VersionFile => Path.Combine(RuntimeDir, "runtime-version.txt");

    /// <summary>Where downloaded GGUF models live.</summary>
    public static string ModelsDir => Path.Combine(AppPaths.LocalDir, "translate", "models");

    /// <summary>Scratch space for in-flight model downloads.</summary>
    public static string DownloadsDir => Path.Combine(AppPaths.LocalDir, "translate", "downloads");

    /// <summary>
    /// True when the runtime is present. The app still builds and runs without it (it is a generated
    /// artefact, see tools/build-llama-runtime.ps1); the translation UI then reports it as
    /// unavailable instead of failing.
    /// </summary>
    public static bool IsRuntimeInstalled => File.Exists(ServerExe);

    /// <summary>Reads the runtime version stamp, or an empty string when it is missing.</summary>
    public static string ReadRuntimeVersion()
    {
        try
        {
            if (File.Exists(VersionFile)) return File.ReadAllText(VersionFile).Trim().Replace("\r\n", " · ");
        }
        catch (IOException) { /* a missing stamp only costs a label */ }
        return string.Empty;
    }
}
