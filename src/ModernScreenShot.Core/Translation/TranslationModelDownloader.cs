using System.IO;
using System.Net.Http;

namespace ModernScreenShot.Core.Translation;

/// <summary>
/// Fetches the Hy-MT2 GGUF model on demand into %LOCALAPPDATA%\Modern-ScreenShot\translate\models.
///
/// One file covers all 36 languages and both directions (unlike the previous Argos engine, which
/// needed a ~70 MB package per pair). It is large, so the download is resumable: a partial file is
/// kept and the next attempt continues from where it stopped, which matters on a slow link. The
/// completed file is only published once its byte count matches the size the host reports.
/// </summary>
public static class TranslationModelDownloader
{
    public const string ModelFileName = "Hy-MT2-1.8B-Q4_K_M.gguf";

    /// <summary>Verified length of the published file; a mismatch means a truncated download.</summary>
    public const long ModelBytes = 1_133_080_448;

    // ModelScope is the primary host: measured ~5.7 MB/s here against ~0.3 MB/s from the HuggingFace
    // CDN for the same file. HuggingFace stays as the fallback.
    private static readonly string[] Sources =
    [
        "https://modelscope.cn/models/Tencent-Hunyuan/Hy-MT2-1.8B-GGUF/resolve/master/" + ModelFileName,
        "https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF/resolve/main/" + ModelFileName,
    ];

    public static string ModelPath => Path.Combine(TranslationEngine.ModelsDir, ModelFileName);

    /// <summary>True when the model is already on disk at its full size.</summary>
    public static bool IsModelInstalled()
    {
        var info = new FileInfo(ModelPath);
        return info.Exists && info.Length == ModelBytes;
    }

    /// <summary>Bytes already downloaded, so the UI can show a resume-aware progress bar.</summary>
    public static long InstalledBytes()
    {
        var info = new FileInfo(ModelPath);
        return info.Exists ? info.Length : 0;
    }

    /// <summary>Removes the model (and any partial file). Returns false when there was nothing to remove.</summary>
    public static bool Uninstall()
    {
        bool removed = false;
        foreach (string path in new[] { ModelPath, ModelPath + ".part" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                removed = true;
            }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
        return removed;
    }

    /// <summary>
    /// Downloads the model, resuming a previous partial file when one exists.
    /// <paramref name="progress"/> reports 0..1 across the whole file.
    /// </summary>
    public static async Task DownloadAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(TranslationEngine.ModelsDir);
        Directory.CreateDirectory(TranslationEngine.DownloadsDir);

        if (IsModelInstalled())
        {
            progress?.Report(1.0);
            return;
        }

        // Download into the models folder next to the final name: the .part file then already has the
        // right volume of free space reserved beside it, and the final move is a rename.
        string part = ModelPath + ".part";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ModernScreenShot/1.0");

        Exception? lastError = null;
        foreach (string url in Sources)
        {
            try
            {
                await DownloadFromAsync(http, url, part, progress, ct);
                lastError = null;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                // Keep the partial file: the next source resumes from the same offset.
            }
        }
        if (lastError is not null) throw lastError;

        var info = new FileInfo(part);
        if (info.Length != ModelBytes)
        {
            throw new IOException(
                $"The model download is incomplete: expected {ModelBytes} bytes but got {info.Length}. "
                + "Run the download again to resume.");
        }
        File.Move(part, ModelPath, overwrite: true);
        progress?.Report(1.0);
    }

    private static async Task DownloadFromAsync(
        HttpClient http, string url, string part, IProgress<double>? progress, CancellationToken ct)
    {
        long have = new FileInfo(part) is { Exists: true } existing ? existing.Length : 0;
        if (have > ModelBytes)
        {
            // A stale partial from a different build can never complete; start over.
            File.Delete(part);
            have = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // A server that ignores Range answers 200 with the whole file, so restart from zero.
        bool resuming = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resuming && have > 0)
        {
            File.Delete(part);
            have = 0;
        }

        long total = (response.Content.Headers.ContentLength ?? 0) + have;
        if (total <= 0) total = ModelBytes;
        progress?.Report((double)have / ModelBytes);

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = new FileStream(
            part, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[1 << 20];
        long written = have, reportedAt = have;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            if (written - reportedAt >= 4 << 20) // throttle progress to ~4 MB steps
            {
                reportedAt = written;
                progress?.Report((double)written / ModelBytes);
            }
        }
        await destination.FlushAsync(ct);
        progress?.Report((double)written / ModelBytes);
    }
}
