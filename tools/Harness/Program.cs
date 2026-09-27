using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModernScreenShot.Core.Annotation;
using ModernScreenShot.Core.Imaging;
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

        var big = Sample(3840, 2160);
        var sw4k = Stopwatch.StartNew();
        EffectPipeline.Compose(big, BuiltInPresets.Mirror().Settings);
        sw4k.Stop();
        Check(sw4k.ElapsedMilliseconds < 3000, $"4K Mirror pipeline {sw4k.ElapsedMilliseconds} ms");

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
    }
}
