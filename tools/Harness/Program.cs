using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModernScreenShot.Core.Annotation;
using ModernScreenShot.Core.History;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Ocr;
using ModernScreenShot.Core.Output;
using ModernScreenShot.Core.Settings;

namespace Harness;

internal static class Program
{
    private static int _failures;
    private static readonly string OutDir = Path.Combine(AppContext.BaseDirectory, "out");

    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(OutDir);
        var cmd = args.Length > 0 ? args[0] : "core";
        switch (cmd)
        {
            case "core": Core(); break;
            case "ocr": OcrTest(args.Length > 1 ? args[1] : null); break;
            case "ocr-download": OcrDownloadTest(); break;
            default: Console.WriteLine($"Unknown command {cmd}"); return 2;
        }
        Console.WriteLine(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILURE(S)");
        Console.WriteLine($"Output: {OutDir}");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(bool cond, string name)
    {
        Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name}");
        if (!cond) _failures++;
    }

    private static PixelBuffer Sample(int w, int h)
    {
        var b = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                b.SetPixel(x, y, new PixelColor(255, (byte)(x * 255 / w), (byte)(y * 255 / h), (byte)(((x / 20 + y / 20) % 2) * 120 + 60)));
        return b;
    }

    private static void Save(PixelBuffer b, string name)
    {
        var src = BitmapSource.Create(b.Width, b.Height, 96, 96, PixelFormats.Bgra32, null, b.Data, b.Stride);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var fs = File.Create(Path.Combine(OutDir, name));
        enc.Save(fs);
    }

    /// <summary>Renders text onto a white canvas (the shape a screenshot of UI text has) so the OCR
    /// test is deterministic and asserts against a known ground truth instead of a fixture file.</summary>
    private static PixelBuffer RenderText(string text, int fontSize = 34)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            fontSize, Brushes.Black, 96);
        int w = (int)Math.Ceiling(formatted.Width) + 60;
        int h = (int)Math.Ceiling(formatted.Height) + 60;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawText(formatted, new Point(30, 30));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        int stride = w * 4;
        var data = new byte[stride * h];
        rtb.CopyPixels(data, stride, 0);
        return new PixelBuffer(w, h, data);
    }

    private static PixelBuffer LoadImageFile(string path)
    {
        var frame = BitmapFrame.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight, stride = w * 4;
        var data = new byte[stride * h];
        converted.CopyPixels(data, stride, 0);
        return new PixelBuffer(w, h, data);
    }

    /// <summary>
    /// End-to-end offline OCR: bundled-model presence, Chinese text WITH full-width punctuation,
    /// latin text and digits, and the fast tier's timing. Pass an image path to additionally run
    /// the recognizer over a real screenshot and dump the text to out/ocr-file.txt.
    /// </summary>
    /// <summary>
    /// End-to-end hop of the accurate-tier downloader (the code path behind the settings window's
    /// download button). Heavy — ~165 MB — so it is a separate command from the regular OCR test.
    /// </summary>
    private static void OcrDownloadTest()
    {
        Console.WriteLine($"Accurate models target: {OcrModelDownloader.AccurateModelsDir}");
        Console.WriteLine($"Total size: {OcrModelDownloader.TotalBytes / (1024.0 * 1024.0):0.0} MB");
        if (OcrModelDownloader.AllModelsPresent())
        {
            Console.WriteLine("Already installed.");
            return;
        }
        int lastDecile = -1;
        var progress = new Progress<double>(fraction =>
        {
            int decile = (int)(fraction * 10);
            if (decile != lastDecile)
            {
                lastDecile = decile;
                Console.WriteLine($"  {fraction * 100:0}%");
            }
        });
        var watch = Stopwatch.StartNew();
        OcrModelDownloader.DownloadAsync(progress).GetAwaiter().GetResult();
        watch.Stop();
        Console.WriteLine($"Downloaded in {watch.Elapsed.TotalSeconds:0.0} s");
        Check(OcrModelDownloader.AllModelsPresent(), "accurate models present after download");
    }

    private static void OcrTest(string? imagePath)
    {
        using var service = new OcrService();
        Check(service.HasBundledModels, $"bundled OCR models present ({OcrService.DefaultBundleDir})");
        if (!service.HasBundledModels) return;

        var img = RenderText("截图工具 OCR 测试：你好，世界！Hello, World! (2026)");
        Save(img, "ocr-input.png");
        var first = Stopwatch.StartNew();
        var result = service.Recognize(img, OcrAccuracy.Fast);
        first.Stop();
        File.WriteAllText(Path.Combine(OutDir, "ocr.txt"), result.Text);
        Console.WriteLine($"--- OCR fast ({result.ElapsedMs} ms inference, {first.ElapsedMilliseconds} ms incl. model load) ---");
        Console.WriteLine(result.Text);
        Console.WriteLine("---");

        string flat = result.Text.Replace(" ", "");
        Check(flat.Contains("你好，世界！"), $"reads Chinese with full-width punctuation ('{flat}')");
        Check(flat.Contains("Hello") && flat.Contains("2026"), $"reads latin text and digits ('{flat}')");
        Check(result.Lines.Count >= 1 && result.Lines.All(l => l.Score > 0.5), $"line confidences sane ({result.Lines.Count} line(s))");
        Check(result.AccurateRequested == false && !result.FallbackToFast, "fast tier reported as requested");

        // The fast tier is the always-available path; keep a loose bound so CI machines with cold
        // file caches don't flake while a catastrophic slowdown (minutes) still fails the suite.
        Check(first.ElapsedMilliseconds < 30000, $"first recognition incl. model load under 30 s ({first.ElapsedMilliseconds} ms)");
        var warm = Stopwatch.StartNew();
        var second = service.Recognize(img, OcrAccuracy.Fast);
        warm.Stop();
        Check(second.Text == result.Text, "warm run is stable");
        Console.WriteLine($"warm recognition: {warm.ElapsedMilliseconds} ms (reported {second.ElapsedMs} ms)");

        // Accurate tier: only assert the fallback contract when the server models aren't installed
        // (they are an optional on-demand download); if installed, run and check it recognizes too.
        if (!service.HasAccurateModels)
        {
            var fallback = service.Recognize(img, OcrAccuracy.Accurate);
            Check(fallback.FallbackToFast && fallback.Text.Contains("你好"), "accurate without models falls back to fast");
        }
        else
        {
            var accurate = service.Recognize(img, OcrAccuracy.Accurate);
            Check(!accurate.FallbackToFast && accurate.Text.Replace(" ", "").Contains("你好，世界！"), "accurate tier recognizes");
            Console.WriteLine($"accurate recognition: {accurate.ElapsedMs} ms");
        }

        // Steady-state speed: three warm runs; the best one approximates what a user sees once the
        // engine and file caches are hot.
        long best = long.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            var run = service.Recognize(img, OcrAccuracy.Fast);
            best = Math.Min(best, run.ElapsedMs);
        }
        Console.WriteLine($"warm steady-state best: {best} ms");

        // TEMP-EXPERIMENT: thread-count sweep to pick the production default.
        if (Environment.GetEnvironmentVariable("MSS_OCR_THREAD_SWEEP") == "1")
        {
            string dir = OcrService.DefaultBundleDir;
            string det = Path.Combine(dir, "ch_PP-OCRv5_mobile_det.onnx");
            string cls = Path.Combine(dir, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
            string rec = Path.Combine(dir, "ch_PP-OCRv5_rec_mobile.onnx");
            string dict = Path.Combine(dir, "ppocrv5_dict.txt");
            using var sk = SKBitmapFrom(img);
            foreach (int threads in new[] { 1, 2, 4, 6, 8, 0 })
            {
                using var opts = RapidOcrNet.RapidOcr.GetDefaultSessionOptions(threads);
                using var engine = new RapidOcrNet.RapidOcr();
                engine.InitModels(det, cls, rec, dict, opts);
                var times = new List<long>();
                for (int i = 0; i < 3; i++)
                {
                    var swi = Stopwatch.StartNew();
                    engine.Detect(sk, RapidOcrNet.RapidOcrOptions.Default);
                    swi.Stop();
                    times.Add(swi.ElapsedMilliseconds);
                }
                Console.WriteLine($"threads={threads}: runs={string.Join("/", times)} ms");
            }
        }

        if (imagePath is { Length: > 0 })
        {
            var fileImage = LoadImageFile(imagePath);
            var fileResult = service.Recognize(fileImage, OcrAccuracy.Fast);
            File.WriteAllText(Path.Combine(OutDir, "ocr-file.txt"), fileResult.Text);
            Console.WriteLine($"--- OCR of {imagePath} ({fileImage.Width}x{fileImage.Height}, {fileResult.ElapsedMs} ms) — see out/ocr-file.txt ---");
        }
    }

    private static SkiaSharp.SKBitmap SKBitmapFrom(PixelBuffer image)
    {
        var bmp = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(image.Width, image.Height,
            SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(image.Data, 0, bmp.GetPixels(), image.Data.Length);
        return bmp;
    }

    private static void Core()
    {
        var img = Sample(400, 260);

        var so = new ShadowOptions { BlurRadius = 24, Distance = 12, Angle = 90, Opacity = 0.5 };
        var sh = ShadowEffect.Apply(img, so, out int ox, out int oy);
        Save(sh, "shadow.png");
        Check(sh.Width > img.Width && sh.Height > img.Height, "shadow canvas larger than source");
        Check(sh.GetPixel(ox + img.Width / 2, oy + img.Height + 6).A > 0, "shadow visible below image");
        Check(sh.GetPixel(0, 0).A == 0, "shadow far corner transparent");
        Check(oy < sh.Height - (oy + img.Height), "downward shadow has larger bottom margin");
        Check(sh.GetPixel(ox + 10, oy + 10) == img.GetPixel(10, 10), "image composited unchanged over shadow");

        var ro = new ReflectionOptions { Enabled = true, Height = 0.4, Gap = 4 };
        var rf = ReflectionEffect.Apply(img, ro);
        Save(rf, "reflection.png");
        Check(rf.Height == img.Height + 4 + (int)Math.Round(img.Height * 0.4), "reflection height");
        var mirrored = rf.GetPixel(50, img.Height + 4);
        Check(mirrored.A > 0 && mirrored.R == img.GetPixel(50, img.Height - 1).R, "reflection mirrors bottom row");
        Check(rf.GetPixel(50, rf.Height - 1).A < 10, "reflection fades out");

        var rc = FrameEffect.RoundCorners(img, 16);
        Check(rc.GetPixel(0, 0).A == 0 && rc.GetPixel(200, 130).A == 255, "rounded corners");
        Check(rc.GetPixel(2, 16).A is > 0 and < 255 || rc.GetPixel(3, 5).A is > 0 and < 255, "rounded corner antialiasing");

        foreach (var preset in BuiltInPresets.All())
        {
            var sw = Stopwatch.StartNew();
            var outImg = EffectPipeline.Compose(img, preset.Settings);
            sw.Stop();
            Save(outImg, $"preset_{preset.Name.Replace("Preset.", "")}.png");
            Check(outImg.Width >= img.Width, $"pipeline {preset.Name} ({sw.ElapsedMilliseconds} ms, {outImg.Width}x{outImg.Height})");
        }

        // Mac-style window shadow (capture-time bake): tight surround, visible contact edge that
        // fades monotonically, and a shadow that follows the source silhouette — not a flat slab.
        var mac = BuiltInPresets.MacShadow();
        var macWin = FrameEffect.RoundCorners(img, mac.Frame.CornerRadius);
        var macShadow = ShadowEffect.RenderShadowOnly(macWin, mac.Shadow, out int mox, out int moy);
        Check(macShadow is not null && macShadow.Width - macWin.Width == 118,
            $"mac shadow surround sized ({macShadow?.Width - macWin.Width ?? -1}px, margin 59 per side)");
        int midY = moy + 130;
        int aContact = macShadow!.GetPixel(mox - 2, midY).A;
        int a15 = macShadow.GetPixel(mox - 15, midY).A;
        int a30 = macShadow.GetPixel(mox - 30, midY).A;
        int a55 = macShadow.GetPixel(mox - 55, midY).A;
        Check(aContact >= 25, $"mac contact shadow visible at window edge ({aContact}/255)");
        Check(aContact > a15 && a15 > a30 && a30 >= a55, $"mac shadow falls off outward ({aContact}>{a15}>{a30}>={a55})");
        Check(a55 <= 12, $"mac shadow fades before the surround ends ({a55}/255 at 55px)");
        var macOut = EffectPipeline.Compose(macWin, mac);
        Check(macOut.Width == macShadow.Width && macOut.Height == macShadow.Height, "mac pipeline adds no padding beyond the shadow margins");
        // Over representative backgrounds so the falloff can be eyeballed.
        foreach (var (bgName, br, bg, bb) in new[] { ("white", (byte)255, (byte)255, (byte)255), ("dark", (byte)32, (byte)38, (byte)46) })
        {
            var onBg = new PixelBuffer(macOut.Width, macOut.Height);
            var bgPixel = new PixelColor(255, br, bg, bb);
            for (int y = 0; y < onBg.Height; y++)
                for (int x = 0; x < onBg.Width; x++)
                    onBg.SetPixel(x, y, bgPixel);
            onBg.DrawOver(macOut, 0, 0);
            Save(onBg, $"mac_on_{bgName}.png");
        }
        var notched = new PixelBuffer(320, 200);
        for (int y = 0; y < 200; y++)
            for (int x = 0; x < 320; x++)
                notched.SetPixel(x, y, new PixelColor(255, 200, 200, 200));
        for (int y = 0; y < 70; y++)
            for (int x = 220; x < 320; x++)
                notched.SetPixel(x, y, new PixelColor(0, 0, 0, 0));
        var nsh = ShadowEffect.RenderShadowOnly(notched, mac.Shadow, out int nx, out int ny);
        int underSolid = nsh!.GetPixel(nx + 60, ny + 206).A;
        int underNotch = nsh.GetPixel(nx + 270, ny + 35).A;
        Check(underSolid >= 60 && underSolid > underNotch * 2, $"shadow follows the source silhouette (solid edge {underSolid} vs notch {underNotch})");

        // The 4K buffers below add up to several hundred MB of large-object allocations; release the
        // earlier test buffers first so the suite stays reliable on machines under commit pressure.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        var big = Sample(3840, 2160);
        var sw4k = Stopwatch.StartNew();
        EffectPipeline.Compose(big, BuiltInPresets.Mirror().Settings);
        sw4k.Stop();
        Check(sw4k.ElapsedMilliseconds < 3000, $"4K Mirror pipeline {sw4k.ElapsedMilliseconds} ms");
        big = null;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        var mos = img.Clone();
        Mosaic.Pixelate(mos, new PixelRect(20, 20, 100, 100), 10);
        Check(mos.GetPixel(21, 21) == mos.GetPixel(29, 29), "pixelate cell uniform");
        Mosaic.Blur(mos, new PixelRect(200, 20, 100, 100), 10);
        Save(mos, "mosaic.png");
        Check(mos.GetPixel(0, 0) == img.GetPixel(0, 0), "mosaic outside rect untouched");

        var page = Sample(200, 1200);
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < 200; x++)
                if ((x + y * 7) % 13 == 0) page.SetPixel(x, y, PixelColor.Black);
        PixelBuffer Frame(int scroll)
        {
            var f = new PixelBuffer(200, 300);
            for (int y = 0; y < 30; y++) f.Row(y).Fill(200);
            for (int y = 30; y < 300; y++) page.Row(scroll + y - 30).CopyTo(f.Row(y));
            return f;
        }
        var st = new ScrollStitcher();
        st.AddFrame(Frame(0));
        st.AddFrame(Frame(120));
        st.AddFrame(Frame(240));
        bool same = st.AddFrame(Frame(240));
        Check(!same, "identical frame detected");
        Check(st.Height == 30 + 270 + 240, $"stitched height {st.Height} == 540");
        Check(st.UnmatchedFrames == 0, "no unmatched frames");
        Check(st.Result!.GetPixel(5, 30 + 400).Equals(page.GetPixel(5, 400)), "stitched content correct");
        Save(st.Result!, "stitched.png");

        var name = FileNameTemplate.Format("Shot_{yyyy}{MM}{dd}_{counter:000}_{window}", new DateTime(2026, 9, 27, 1, 2, 3), 7, "a<b>:c", "Region");
        Check(name == "Shot_20260927_007_a_b__c", $"template '{name}'");
        Check(FileNameTemplate.Sanitize("CON") == "_CON", "reserved name");
        Check(FileNameTemplate.Sanitize("   ") == "Screenshot", "empty name");

        var hsv = ColorUtil.ToHsv(new PixelColor(255, 255, 0, 0));
        Check(Math.Abs(hsv.h) < 0.01 && ColorUtil.FromHsv(120, 1, 1).G == 255, "hsv");

        // Black-frame detector: the single near-black definition behind the capture fallbacks and
        // the presentation guards. Guards the exact regression class the black-window bug came from.
        var allBlack = new PixelBuffer(320, 200); // ctor default pixels are (0,0,0,0) = near-black
        Check(BlackFrame.NearBlackFraction(allBlack) == 1.0, "black detector: all-black frame = 1.0");
        var allWhite = new PixelBuffer(320, 200);
        for (int y = 0; y < allWhite.Height; y++)
            for (int x = 0; x < allWhite.Width; x++)
                allWhite.SetPixel(x, y, new PixelColor(255, 255, 255, 255));
        Check(BlackFrame.NearBlackFraction(allWhite) == 0.0, "black detector: all-white frame = 0.0");
        var half = new PixelBuffer(320, 200);
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 320; x++)
                half.SetPixel(x, y, new PixelColor(255, 255, 255, 255)); // top half white, bottom half black
        double halfFrac = BlackFrame.NearBlackFraction(half);
        Check(Math.Abs(halfFrac - 0.5) < 0.02, $"black detector: half-black frame ≈ 0.5 ({halfFrac:F3})");
        Check(BlackFrame.NearBlackFraction(Sample(320, 200)) < 0.60, "black detector: normal content is not 'suspect black'");
        Check(BlackFrame.NearBlackFraction(new byte[10 * 10 * 4], 10, 10) == 1.0, "black detector: zeroed byte buffer = 1.0");
        // Dark-theme background must NOT count as black (sum 96 > 30), otherwise every dark UI
        // screenshot would trip the capture fallbacks.
        var darkTheme = new PixelBuffer(160, 120);
        for (int y = 0; y < 120; y++)
            for (int x = 0; x < 160; x++)
                darkTheme.SetPixel(x, y, new PixelColor(255, 0x20, 0x20, 0x20));
        Check(BlackFrame.NearBlackFraction(darkTheme) == 0.0, "black detector: #202020 background is not near-black");

        var doc = new AnnotationDocument { ImageWidth = 10, ImageHeight = 10 };
        doc.Items.Add(new ArrowItem { Start = new(1, 2), End = new(3, 4) });
        doc.Items.Add(new PenItem { Points = [new(1, 1), new(2, 2)] });
        doc.Items.Add(new StepItem());
        doc.Items.Add(new StepItem());
        doc.RenumberSteps();
        doc.Crop = new PixelRect(1, 1, 5, 5);
        var round = UndoStack.Deserialize(UndoStack.Serialize(doc));
        Check(round.Items[0] is ArrowItem && round.Items[1] is PenItem { Points.Count: 2 } && round.Items.OfType<StepItem>().Last().Number == 2 && round.Crop?.Width == 5, "annotation json roundtrip");

        var undo = new UndoStack();
        undo.Push(doc);
        doc.Items.RemoveAt(0);
        var restored = undo.Undo(doc)!;
        Check(restored.Items.Count == 4 && undo.CanRedo, "undo");

        var tmp = Path.Combine(OutDir, "settings-test.json");
        var store = new SettingsStore(tmp);
        store.Load();
        store.Current.Output.JpgQuality = 77;
        store.Save();
        var store2 = new SettingsStore(tmp);
        Check(store2.Load().Output.JpgQuality == 77 && store2.Current.Hotkeys.Bindings.Count == HotkeyActions.All.Length, "settings roundtrip");
        File.WriteAllText(tmp, "{ not json");
        Check(new SettingsStore(tmp).Load().Output.JpgQuality == 92, "corrupt settings fallback");

        // Normalize hardening: out-of-range values and explicit JSON nulls (a hand-edited file that
        // still parses reaches these verbatim) must be repaired, not crash the first consumer.
        var probe = new AppSettings();
        probe.Output.JpgQuality = 500;
        probe.Hotkeys.Bindings["Region"] = null!;
        probe.Editor.Palette = null!;
        probe.UserPresets.Add(new EffectPreset { Name = null!, Settings = null! });
        var norm = new SettingsStore(Path.Combine(OutDir, "normalize-test.json"));
        norm.Replace(probe);
        Check(norm.Current.Output.JpgQuality == 100, "normalize clamps jpg quality");
        Check(norm.Current.Hotkeys.Bindings["Region"] is not null, "normalize repairs null hotkey binding");
        Check(norm.Current.Editor.Palette is { Count: > 0 }, "normalize repairs null editor palette");
        Check(norm.Current.UserPresets[0].Settings is not null && norm.Current.UserPresets[0].Name == "", "normalize repairs null user preset");

        // History store semantics: 0 = unlimited, prune trims to the newest, Clear fires Changed
        // exactly once (HistoryWindow rebuilds its whole grid per event), and a meta-less orphan
        // from a crashed write is swept instead of accumulating forever.
        var histRoot = Path.Combine(OutDir, "history-test");
        if (Directory.Exists(histRoot)) Directory.Delete(histRoot, true);
        var hist = new HistoryStore(histRoot);
        int changed = 0;
        hist.Changed += (_, _) => changed++;
        void AddEntry()
        {
            var e = hist.Create(10, 10, null, "Region");
            File.WriteAllText(e.OriginalPath, "x"); // List only shows entries with original.png present
            hist.SaveMeta(e);
        }
        AddEntry();
        AddEntry();
        changed = 0;
        hist.Prune(0);
        Check(changed == 0 && hist.List().Count == 2, "history: prune(0) keeps everything");
        hist.Prune(1);
        Check(changed == 1 && hist.List().Count == 1, "history: prune(1) trims to the newest entry");
        AddEntry();
        changed = 0;
        hist.Clear();
        Check(changed == 1 && hist.List().Count == 0, "history: clear fires Changed once and empties the store");
        var orphan = Directory.CreateDirectory(Path.Combine(histRoot, "orphan"));
        File.WriteAllText(Path.Combine(orphan.FullName, "original.png"), "x");
        Directory.SetLastWriteTimeUtc(orphan.FullName, DateTime.UtcNow - TimeSpan.FromHours(1));
        hist.Prune(5);
        Check(!Directory.Exists(orphan.FullName), "history: meta-less orphan swept");

        // In-place unmanaged probe (ScreenCapturer's DIB path) must agree with the managed buffer path.
        IntPtr unmanaged = System.Runtime.InteropServices.Marshal.AllocHGlobal(320 * 200 * 4);
        try
        {
            for (int i = 0; i < 320 * 200; i++)
                System.Runtime.InteropServices.Marshal.WriteInt32(unmanaged, i * 4, unchecked((int)0xFFFFFFFF));
            Check(BlackFrame.NearBlackFraction(unmanaged, 320, 200) == 0.0, "black detector: in-place white DIB bits = 0.0");
            for (int i = 0; i < 320 * 200; i++)
                System.Runtime.InteropServices.Marshal.WriteInt32(unmanaged, i * 4, 0);
            Check(BlackFrame.NearBlackFraction(unmanaged, 320, 200) == 1.0, "black detector: in-place black DIB bits = 1.0");
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(unmanaged);
        }
    }
}
