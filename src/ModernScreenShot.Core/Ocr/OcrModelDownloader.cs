using System.IO;
using System.Net.Http;
using ModernScreenShot.Core.Settings;

namespace ModernScreenShot.Core.Ocr;

/// <summary>
/// Fetches the PP-OCRv5 server (accurate) detection + recognition models on demand. Files land in
/// %LOCALAPPDATA%\Modern-ScreenShot\models\ocr; once present, the accurate tier runs fully offline.
/// Downloads are written to a .part file and only moved into place after a byte-count check, so a
/// truncated or error-page response can never masquerade as a model.
/// </summary>
public static class OcrModelDownloader
{
    public const string DetServerFile = "ch_PP-OCRv5_det_server.onnx";
    public const string RecServerFile = "ch_PP-OCRv5_rec_server.onnx";

    private const string ModelRoot = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv5";

    /// <summary>Where the accurate models live (mirrors what OcrService reads).</summary>
    public static string AccurateModelsDir => Path.Combine(AppPaths.LocalDir, "models", "ocr");

    // Sizes are the verified byte lengths on the model host; a mismatch after download is treated
    // as a failed download instead of a usable model.
    private static readonly (string FileName, string Url, long Size)[] Files =
    [
        (DetServerFile, $"{ModelRoot}/det/{DetServerFile}", 88_118_768),
        (RecServerFile, $"{ModelRoot}/rec/{RecServerFile}", 84_577_022),
    ];

    public static long TotalBytes { get { long t = 0; foreach (var f in Files) t += f.Size; return t; } }

    /// <summary>True when every accurate model file is already on disk with the expected size.</summary>
    public static bool AllModelsPresent()
    {
        foreach (var (fileName, _, size) in Files)
        {
            var info = new FileInfo(Path.Combine(AccurateModelsDir, fileName));
            if (!info.Exists || info.Length != size) return false;
        }
        return true;
    }

    /// <summary>Downloads the missing model files. <paramref name="progress"/> reports 0..1 overall.</summary>
    public static async Task DownloadAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(AccurateModelsDir);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ModernScreenShot/1.0");

        long totalBytes = TotalBytes;
        long completedBefore = 0;
        foreach (var (fileName, url, size) in Files)
        {
            string target = Path.Combine(AccurateModelsDir, fileName);
            if (new FileInfo(target) is { Exists: true } existing && existing.Length == size)
            {
                completedBefore += size;
                progress?.Report((double)completedBefore / totalBytes);
                continue;
            }

            string part = target + ".part";
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var destination = File.Create(part);
                var buffer = new byte[1 << 20];
                long fileBytes = 0;
                long reportedAt = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    fileBytes += read;
                    if (fileBytes - reportedAt >= 2 << 20) // throttle progress to ~2 MB steps
                    {
                        reportedAt = fileBytes;
                        progress?.Report((double)(completedBefore + fileBytes) / totalBytes);
                    }
                }
                await destination.FlushAsync(ct);
            }
            catch
            {
                TryDelete(part);
                throw;
            }

            var info = new FileInfo(part);
            if (info.Length != size)
            {
                TryDelete(part);
                throw new IOException($"Model download is incomplete: {fileName} should be {size} bytes but got {info.Length}.");
            }
            File.Move(part, target, overwrite: true);
            completedBefore += size;
            progress?.Report((double)completedBefore / totalBytes);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort: a leftover .part is overwritten on the next try */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }
}
