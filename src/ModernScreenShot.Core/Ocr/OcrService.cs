using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Settings;
using RapidOcrNet;
using SkiaSharp;

namespace ModernScreenShot.Core.Ocr;

/// <summary>
/// Recognition tier. Fast runs the mobile models bundled with the app; Accurate runs the PP-OCRv5
/// server pair that is downloaded on demand (see <see cref="OcrModelDownloader"/>) and falls back
/// to Fast while those files are missing.
/// </summary>
public enum OcrAccuracy { Fast, Accurate }

/// <summary>One recognized text line plus its average per-character confidence (0..1) and the box
/// the detector reported it in. The box matters when the detector splits a line into word-sized
/// fragments — see <see cref="OcrTextLayout"/>.</summary>
public sealed record OcrLine(string Text, double Score, PixelRect Bounds = default);

/// <summary>Result of one recognition pass over one image.</summary>
public sealed class OcrTextResult
{
    /// <summary>All lines joined with newlines; what gets placed on the clipboard.</summary>
    public required string Text { get; init; }
    public required IReadOnlyList<OcrLine> Lines { get; init; }
    public long ElapsedMs { get; init; }
    /// <summary>True when the caller asked for the accurate tier (which may still have fallen back).</summary>
    public bool AccurateRequested { get; init; }
    /// <summary>Accurate was requested but its models are not installed; the fast model ran instead.</summary>
    public bool FallbackToFast { get; init; }
}

/// <summary>
/// Offline Chinese + English OCR (PP-OCRv5 via ONNX Runtime, through RapidOcrNet). The whole
/// pipeline runs locally — no network after install. The engine is created lazily on first use
/// (loading it costs ~1 s and a few tens of MB, so idle tray sessions never pay for it) and is
/// re-created when the accuracy tier changes. ONNX sessions are not safe for concurrent Run calls
/// from the same engine, so <see cref="Recognize"/> serializes on a lock; callers on the UI thread
/// should use <see cref="RecognizeAsync"/>.
/// </summary>
public sealed class OcrService : IDisposable
{
    // Bundled mobile pipeline (detection + classifier from the RapidOcrNet package, Chinese
    // recognition model + dictionary vendored under Assets/ocr — see the csproj).
    private const string DetMobileFile = "ch_PP-OCRv5_mobile_det.onnx";
    private const string ClsTextlineFile = "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx";
    private const string RecMobileFile = "ch_PP-OCRv5_rec_mobile.onnx";
    private const string ChineseDictFile = "ppocrv5_dict.txt";

    private readonly string _bundleDir;
    private readonly string _accurateDir;
    private readonly object _gate = new();
    private RapidOcr? _engine;
    private SessionOptions? _sessionOptions;
    private OcrAccuracy? _loadedAccuracy;

    /// <param name="bundleDir">Folder holding the mobile pipeline (defaults to models/v5 next to the app).</param>
    /// <param name="accurateDir">Folder holding the downloaded server models (defaults to %LOCALAPPDATA%).</param>
    public OcrService(string? bundleDir = null, string? accurateDir = null)
    {
        _bundleDir = bundleDir ?? DefaultBundleDir;
        _accurateDir = accurateDir ?? OcrModelDownloader.AccurateModelsDir;
    }

    /// <summary>Where the pipeline shipped with the app lives (filled by the build).</summary>
    public static string DefaultBundleDir => Path.Combine(AppContext.BaseDirectory, "models", "v5");

    /// <summary>The folder this instance reads the bundled pipeline from.</summary>
    public string BundleModelsDir => _bundleDir;

    /// <summary>The fast tier can run: all four bundled model files are present.</summary>
    public static bool BundledModelsPresent(string bundleDir) =>
        File.Exists(Path.Combine(bundleDir, DetMobileFile)) &&
        File.Exists(Path.Combine(bundleDir, ClsTextlineFile)) &&
        File.Exists(Path.Combine(bundleDir, RecMobileFile)) &&
        File.Exists(Path.Combine(bundleDir, ChineseDictFile));

    /// <summary>The accurate tier has been downloaded and can run.</summary>
    public static bool AccurateModelsPresent(string accurateDir) =>
        File.Exists(Path.Combine(accurateDir, OcrModelDownloader.DetServerFile)) &&
        File.Exists(Path.Combine(accurateDir, OcrModelDownloader.RecServerFile));

    public bool HasBundledModels => BundledModelsPresent(_bundleDir);
    public bool HasAccurateModels => AccurateModelsPresent(_accurateDir);

    /// <summary>Recognizes text on a background thread (OCR is CPU-bound; the UI thread must stay free).</summary>
    public Task<OcrTextResult> RecognizeAsync(PixelBuffer image, OcrAccuracy accuracy, CancellationToken ct = default) =>
        Task.Run(() => Recognize(image, accuracy, ct), ct);

    /// <summary>Runs one recognition pass. Serialized across callers; safe to call from any thread.</summary>
    public OcrTextResult Recognize(PixelBuffer image, OcrAccuracy accuracy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();
        if (!HasBundledModels)
            throw new FileNotFoundException("The bundled OCR models are missing next to the app.", _bundleDir);

        lock (_gate)
        {
            bool fallback = accuracy == OcrAccuracy.Accurate && !HasAccurateModels;
            EnsureEngine(fallback ? OcrAccuracy.Fast : accuracy);
            ct.ThrowIfCancellationRequested();

            using var bitmap = ToBitmap(image);
            var sw = Stopwatch.StartNew();
            OcrResult result = _engine!.Detect(bitmap, RapidOcrOptions.Default);
            sw.Stop();

            var lines = ExtractLines(result);
            return new OcrTextResult
            {
                Text = string.Join(Environment.NewLine, lines.Select(l => l.Text)),
                Lines = lines,
                ElapsedMs = sw.ElapsedMilliseconds,
                AccurateRequested = accuracy == OcrAccuracy.Accurate,
                FallbackToFast = fallback,
            };
        }
    }

    /// <summary>Creates (or swaps in) the engine for the requested tier. Caller must hold <see cref="_gate"/>.</summary>
    private void EnsureEngine(OcrAccuracy accuracy)
    {
        if (_engine is not null && _loadedAccuracy == accuracy) return;
        DisposeEngine();

        string det, rec;
        if (accuracy == OcrAccuracy.Accurate)
        {
            det = Path.Combine(_accurateDir, OcrModelDownloader.DetServerFile);
            rec = Path.Combine(_accurateDir, OcrModelDownloader.RecServerFile);
        }
        else
        {
            det = Path.Combine(_bundleDir, DetMobileFile);
            rec = Path.Combine(_bundleDir, RecMobileFile);
        }
        string cls = Path.Combine(_bundleDir, ClsTextlineFile);
        string dict = Path.Combine(_bundleDir, ChineseDictFile);
        foreach (string file in new[] { det, rec, cls, dict })
            if (!File.Exists(file)) throw new FileNotFoundException($"OCR model file is missing: {file}", file);

        // ONNX Runtime's default thread pool (one thread per logical core) is pathologically slow
        // for these small models: measured ~670 ms warm vs ~60 ms on a moderately pinned pool
        // (harness thread sweep, 20+ logical cores). A capped pool is both much faster and much
        // gentler on CPU/memory.
        _sessionOptions = RapidOcr.GetDefaultSessionOptions(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));
        var engine = new RapidOcr();
        try
        {
            engine.InitModels(det, cls, rec, dict, _sessionOptions);
        }
        catch
        {
            engine.Dispose();
            _sessionOptions.Dispose();
            _sessionOptions = null;
            throw;
        }
        _engine = engine;
        _loadedAccuracy = accuracy;
    }

    /// <summary>Copies a straight-alpha BGRA frame into an Skia bitmap (RapidOcrNet's input type).
    /// Transparency only occurs on window captures with masked corners; those pixels composite over
    /// white first so the recognizer sees dark-on-light instead of transparent-as-black noise.</summary>
    private static SKBitmap ToBitmap(PixelBuffer image)
    {
        bool opaque = true;
        for (int i = 3; i < image.Data.Length; i += 4)
        {
            if (image.Data[i] != 255) { opaque = false; break; }
        }
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var source = new SKBitmap(opaque ? info.WithAlphaType(SKAlphaType.Opaque) : info);
        IntPtr dst = source.GetPixels();
        for (int y = 0; y < image.Height; y++)
            Marshal.Copy(image.Data, y * image.Stride, IntPtr.Add(dst, y * source.RowBytes), image.Stride);
        if (opaque) return source;

        var flattened = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(flattened))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, 0, 0);
        }
        source.Dispose();
        return flattened;
    }

    private static List<OcrLine> ExtractLines(OcrResult result)
    {
        var fragments = new List<OcrLine>();
        foreach (var block in result.TextBlocks)
        {
            string text = (block.Text ?? string.Empty).TrimEnd();
            if (text.Length == 0) continue;
            double score = 0;
            if (block.CharScores is { Length: > 0 } scores) score = scores.Average();
            fragments.Add(new OcrLine(text, score, Bounds(block.BoxPoints)));
        }
        // The detector reports boxes, not lines: put word-sized fragments back onto their row so
        // downstream consumers (the result window, translation) see real lines.
        return [.. OcrTextLayout.Assemble(fragments)];
    }

    /// <summary>Bounding box of a detector polygon; empty when the detector reported no points.</summary>
    private static PixelRect Bounds(SKPointI[]? points)
    {
        if (points is not { Length: > 0 }) return default;
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var p in points)
        {
            if (p.X < left) left = p.X;
            if (p.Y < top) top = p.Y;
            if (p.X > right) right = p.X;
            if (p.Y > bottom) bottom = p.Y;
        }
        return PixelRect.FromLTRB(left, top, right, bottom);
    }

    private void DisposeEngine()
    {
        _engine?.Dispose();
        _engine = null;
        _loadedAccuracy = null;
        _sessionOptions?.Dispose();
        _sessionOptions = null;
    }

    public void Dispose()
    {
        lock (_gate) DisposeEngine();
    }
}
