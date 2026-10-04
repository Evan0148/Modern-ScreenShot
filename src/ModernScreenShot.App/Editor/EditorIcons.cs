using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ModernScreenShot.App.Editor;

/// <summary>
/// Vector icons for the editor toolbar, drawn as plain WPF shapes/text so no icon font has to be
/// present. The glyphs match the overlay toolbar's vocabulary so a tool looks the same in the
/// capture overlay and in the full editor. Icons draw white to sit on the dark toolbar strip.
/// </summary>
internal static class EditorIcons
{
    /// <summary>Icon for a tool, sized 18×18 to sit inside a 34×34 toolbar button.</summary>
    public static FrameworkElement For(EditorTool tool) => tool switch
    {
        EditorTool.Select => Select(),
        EditorTool.Rect => Shape(new RectangleGeometry(new Rect(2.5, 4.5, 13, 9))),
        EditorTool.Ellipse => Shape(new EllipseGeometry(new Point(9, 9), 7, 5.5)),
        EditorTool.Line => Shape(Geo(g => { g.BeginFigure(new Point(3, 15), false, false); g.LineTo(new Point(15, 3), true, false); })),
        EditorTool.Arrow => Shape(Arrow()),
        EditorTool.Pen => Pen(),
        EditorTool.Text => Glyph("T", bold: true),
        EditorTool.Step => Step(),
        EditorTool.Highlighter => Highlighter(),
        EditorTool.Mosaic => Mosaic(),
        EditorTool.Blur => Blur(),
        EditorTool.Spotlight => Spotlight(),
        EditorTool.Magnifier => Magnifier(),
        EditorTool.Crop => Crop(),
        EditorTool.Eraser => Eraser(),
        _ => Glyph("?"),
    };

    // ---- action-bar / misc glyphs (name → 18px icon) ----

    public static FrameworkElement Undo() => Shape(Geo(g =>
    {
        g.BeginFigure(new Point(6, 4), false, false);
        g.LineTo(new Point(3, 7), true, false);
        g.LineTo(new Point(6, 10), true, false);
        g.BeginFigure(new Point(3, 7), false, false);
        g.LineTo(new Point(11, 7), true, false);
        g.BezierTo(new Point(15, 7), new Point(15, 14), new Point(10, 14), true, false);
    }));

    public static FrameworkElement Redo() => Mirror(Undo());

    public static FrameworkElement Delete() => Shape(Geo(g =>
    {
        g.BeginFigure(new Point(4, 5), false, false);
        g.LineTo(new Point(14, 5), true, false);
        g.BeginFigure(new Point(7, 5), false, false);
        g.LineTo(new Point(7, 3.5), true, false);
        g.LineTo(new Point(11, 3.5), true, false);
        g.LineTo(new Point(11, 5), true, false);
        g.BeginFigure(new Point(5, 5), false, false);
        g.LineTo(new Point(5.8, 15), true, false);
        g.LineTo(new Point(12.2, 15), true, false);
        g.LineTo(new Point(13, 5), true, false);
    }));

    public static FrameworkElement Front() => LayerGlyph(front: true);
    public static FrameworkElement Back() => LayerGlyph(front: false);

    public static FrameworkElement Fit() => Shape(Geo(g =>
    {
        // four corner brackets (an "expand to fit" mark)
        void Bracket(double cx, double cy, double dx, double dy)
        {
            g.BeginFigure(new Point(cx + dx, cy), false, false);
            g.LineTo(new Point(cx, cy), true, false);
            g.LineTo(new Point(cx, cy + dy), true, false);
        }
        Bracket(3, 3, 4, 4);
        Bracket(15, 3, -4, 4);
        Bracket(3, 15, 4, -4);
        Bracket(15, 15, -4, -4);
    }));

    public static FrameworkElement Copy() => Shape(Geo(g =>
    {
        g.BeginFigure(new Point(6, 3), false, true);
        g.LineTo(new Point(14, 3), true, false);
        g.LineTo(new Point(14, 12), true, false);
        g.LineTo(new Point(6, 12), true, false);
        g.BeginFigure(new Point(4, 6), false, true);
        g.LineTo(new Point(4, 15), true, false);
        g.LineTo(new Point(11, 15), true, false);
    }));

    public static FrameworkElement Save() => Shape(Geo(g =>
    {
        g.BeginFigure(new Point(3, 3), false, true);
        g.LineTo(new Point(12, 3), true, false);
        g.LineTo(new Point(15, 6), true, false);
        g.LineTo(new Point(15, 15), true, false);
        g.LineTo(new Point(3, 15), true, false);
        g.BeginFigure(new Point(6, 3), false, false);
        g.LineTo(new Point(6, 7), true, false);
        g.LineTo(new Point(11, 7), true, false);
        g.LineTo(new Point(11, 3), true, false);
        g.BeginFigure(new Point(6, 15), false, false);
        g.LineTo(new Point(6, 10.5), true, false);
        g.LineTo(new Point(12, 10.5), true, false);
        g.LineTo(new Point(12, 15), true, false);
    }));

    public static FrameworkElement SaveAs()
    {
        var canvas = (Canvas)Save();
        // small pencil accent
        canvas.Children.Add(new Path
        {
            Data = Geo(g => { g.BeginFigure(new Point(12, 13), false, false); g.LineTo(new Point(16.5, 8.5), true, false); }),
            Stroke = Brushes.White,
            StrokeThickness = 1.6,
        });
        return canvas;
    }

    public static FrameworkElement Pin() => Shape(Geo(g =>
    {
        g.BeginFigure(new Point(9, 3), false, false);
        g.LineTo(new Point(9, 10), true, false);
        g.BeginFigure(new Point(5, 10), false, false);
        g.LineTo(new Point(13, 10), true, false);
        g.LineTo(new Point(11.5, 6), true, false);
        g.LineTo(new Point(6.5, 6), true, false);
        g.LineTo(new Point(5, 10), true, false);
        g.BeginFigure(new Point(9, 10), false, false);
        g.LineTo(new Point(9, 15.5), true, false);
    }));

    public static FrameworkElement Effects() => Shape(Geo(g =>
    {
        // sparkle
        g.BeginFigure(new Point(7, 2.5), false, false);
        g.LineTo(new Point(8.4, 6.6), true, false);
        g.LineTo(new Point(12.5, 8), true, false);
        g.LineTo(new Point(8.4, 9.4), true, false);
        g.LineTo(new Point(7, 13.5), true, false);
        g.LineTo(new Point(5.6, 9.4), true, false);
        g.LineTo(new Point(1.5, 8), true, false);
        g.LineTo(new Point(5.6, 6.6), true, false);
        g.LineTo(new Point(7, 2.5), true, false);
        g.BeginFigure(new Point(13.5, 11), false, false);
        g.LineTo(new Point(14.3, 13.2), true, false);
        g.LineTo(new Point(16.5, 14), true, false);
        g.LineTo(new Point(14.3, 14.8), true, false);
        g.LineTo(new Point(13.5, 17), true, false);
        g.LineTo(new Point(12.7, 14.8), true, false);
        g.LineTo(new Point(10.5, 14), true, false);
        g.LineTo(new Point(12.7, 13.2), true, false);
        g.LineTo(new Point(13.5, 11), true, false);
    }));

    public static FrameworkElement Folder() => Shape(Geo(g =>
    {
        g.BeginFigure(new Point(3, 5), false, true);
        g.LineTo(new Point(7, 5), true, false);
        g.LineTo(new Point(8.5, 6.5), true, false);
        g.LineTo(new Point(15, 6.5), true, false);
        g.LineTo(new Point(15, 14), true, false);
        g.LineTo(new Point(3, 14), true, false);
    }));

    public static FrameworkElement ZoomIn() => ZoomGlyph(plus: true);
    public static FrameworkElement ZoomOut() => ZoomGlyph(plus: false);

    /// <summary>OCR action: a document with text lines inside a scan frame — "read the text out of
    /// this". Shared by the editor action bar and the capture overlay toolbar.</summary>
    public static FrameworkElement Ocr() => Shape(Geo(g =>
    {
        // viewfinder corners
        g.BeginFigure(new Point(1.5, 4.5), false, false);
        g.LineTo(new Point(1.5, 1.5), true, false);
        g.LineTo(new Point(4.5, 1.5), true, false);
        g.BeginFigure(new Point(13.5, 1.5), false, false);
        g.LineTo(new Point(16.5, 1.5), true, false);
        g.LineTo(new Point(16.5, 4.5), true, false);
        g.BeginFigure(new Point(16.5, 13.5), false, false);
        g.LineTo(new Point(16.5, 16.5), true, false);
        g.LineTo(new Point(13.5, 16.5), true, false);
        g.BeginFigure(new Point(4.5, 16.5), false, false);
        g.LineTo(new Point(1.5, 16.5), true, false);
        g.LineTo(new Point(1.5, 13.5), true, false);
        // text lines
        g.BeginFigure(new Point(5.5, 6.5), false, false);
        g.LineTo(new Point(12.5, 6.5), true, false);
        g.BeginFigure(new Point(5.5, 9), false, false);
        g.LineTo(new Point(12.5, 9), true, false);
        g.BeginFigure(new Point(5.5, 11.5), false, false);
        g.LineTo(new Point(9.5, 11.5), true, false);
    }));

    /// <summary>Translate action: the familiar "A / 文" pair — Latin and CJK side by side, i.e.
    /// "turn this text into that language". Shared by the editor action bar and the capture
    /// overlay toolbar, and sits directly next to <see cref="Ocr"/> in both.</summary>
    public static FrameworkElement Translate() => TranslatePair();

    // ---- tool glyph builders ----
    // Filled Snipaste-style cursor — kept identical to the overlay toolbar's SelectIcon so both
    // strips share one icon vocabulary.
    private static Canvas Select() => new()
    {
        Width = 18,
        Height = 18,
        Children =
        {
            new System.Windows.Shapes.Path
            {
                Data = Geo(g =>
                {
                    g.BeginFigure(new Point(5.5, 1.5), true, false);
                    g.LineTo(new Point(5.5, 14.5), true, false);
                    g.LineTo(new Point(8.4, 11.6), true, false);
                    g.LineTo(new Point(10.4, 16.2), true, false);
                    g.LineTo(new Point(12.4, 15.3), true, false);
                    g.LineTo(new Point(10.4, 10.8), true, false);
                    g.LineTo(new Point(14.8, 10.3), true, false);
                    g.LineTo(new Point(5.5, 1.5), true, false);
                }),
                Fill = Brushes.White,
            }
        },
    };

    private static Canvas Pen() => Shape(Geo(g =>
    {
        g.BeginFigure(new Point(3, 15), false, false);
        g.LineTo(new Point(4.5, 11.5), true, false);
        g.LineTo(new Point(12, 4), true, false);
        g.LineTo(new Point(14.5, 6.5), true, false);
        g.LineTo(new Point(7, 14), true, false);
        g.LineTo(new Point(3, 15), true, false);
        g.BeginFigure(new Point(11, 5), false, false);
        g.LineTo(new Point(13.5, 7.5), true, false);
    }));

    private static Canvas Highlighter() => Shape(Geo(g =>
    {
        g.BeginFigure(new Point(4, 15), false, false);
        g.LineTo(new Point(4, 12), true, false);
        g.LineTo(new Point(11, 5), true, false);
        g.LineTo(new Point(14, 8), true, false);
        g.LineTo(new Point(7, 15), true, false);
        g.LineTo(new Point(4, 15), true, false);
        g.BeginFigure(new Point(3, 16.5), false, false);
        g.LineTo(new Point(9, 16.5), true, false);
    }));

    private static FrameworkElement Step()
    {
        var canvas = new Canvas { Width = 18, Height = 18 };
        canvas.Children.Add(new Ellipse
        {
            Width = 14, Height = 14, Margin = new Thickness(2),
            Stroke = Brushes.White, StrokeThickness = 1.6,
        });
        canvas.Children.Add(new TextBlock
        {
            Text = "1", FontFamily = new FontFamily("Segoe UI"), FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = Brushes.White, Width = 18, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 2.5, 0, 0),
        });
        return canvas;
    }

    private static FrameworkElement Mosaic()
    {
        var canvas = new Canvas { Width = 18, Height = 18 };
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                canvas.Children.Add(new Rectangle
                {
                    Width = 4.4, Height = 4.4, Margin = new Thickness(2 + x * 5, 2 + y * 5, 0, 0),
                    Fill = (x + y) % 2 == 0 ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                });
        return canvas;
    }

    private static FrameworkElement Blur()
    {
        var canvas = new Canvas { Width = 18, Height = 18 };
        for (int i = 0; i < 3; i++)
        {
            double size = 15 - i * 4.5;
            canvas.Children.Add(new Ellipse
            {
                Width = size, Height = size,
                Margin = new Thickness((18 - size) / 2, (18 - size) / 2, 0, 0),
                Fill = new SolidColorBrush(Color.FromArgb((byte)(0x40 + i * 0x50), 0xFF, 0xFF, 0xFF)),
            });
        }
        return canvas;
    }

    private static Canvas Spotlight() => Shape(Geo(g =>
    {
        // beam
        g.BeginFigure(new Point(9, 2), false, true);
        g.LineTo(new Point(13, 9), true, false);
        g.LineTo(new Point(5, 9), true, false);
        g.LineTo(new Point(9, 2), true, false);
        // pool
        g.BeginFigure(new Point(5, 9), false, false);
        g.BezierTo(new Point(5, 14), new Point(13, 14), new Point(13, 9), true, false);
    }));

    private static FrameworkElement Magnifier()
    {
        var canvas = Shape(Geo(g =>
        {
            g.BeginFigure(new Point(14.5, 15), false, false);
            g.LineTo(new Point(11, 11.5), true, false);
        }));
        canvas.Children.Insert(0, new Ellipse
        {
            Width = 10, Height = 10, Margin = new Thickness(2.5, 2, 0, 0),
            Stroke = Brushes.White, StrokeThickness = 1.6,
        });
        return canvas;
    }

    private static Canvas Crop() => Shape(Geo(g =>
    {
        g.BeginFigure(new Point(5, 2), false, false);
        g.LineTo(new Point(5, 13), true, false);
        g.LineTo(new Point(16, 13), true, false);
        g.BeginFigure(new Point(2, 5), false, false);
        g.LineTo(new Point(13, 5), true, false);
        g.LineTo(new Point(13, 16), true, false);
    }));

    private static Canvas Eraser() => Shape(Geo(g =>
    {
        g.BeginFigure(new Point(3, 12), false, true);
        g.LineTo(new Point(9, 6), true, false);
        g.LineTo(new Point(14, 11), true, false);
        g.LineTo(new Point(11, 14), true, false);
        g.LineTo(new Point(6, 14), true, false);
        g.LineTo(new Point(3, 12), true, false);
        g.BeginFigure(new Point(8, 14), false, false);
        g.LineTo(new Point(5.5, 11.5), true, false);
    }));

    private static FrameworkElement LayerGlyph(bool front)
    {
        var canvas = new Canvas { Width = 18, Height = 18 };
        var back = new Rectangle
        {
            Width = 9, Height = 9, Margin = new Thickness(3, 3, 0, 0), RadiusX = 1, RadiusY = 1,
            Stroke = Brushes.White, StrokeThickness = 1.4,
            Fill = front ? Brushes.Transparent : Brushes.White,
        };
        var fore = new Rectangle
        {
            Width = 9, Height = 9, Margin = new Thickness(6, 6, 0, 0), RadiusX = 1, RadiusY = 1,
            Stroke = Brushes.White, StrokeThickness = 1.4,
            Fill = front ? Brushes.White : Brushes.Transparent,
        };
        canvas.Children.Add(back);
        canvas.Children.Add(fore);
        return canvas;
    }

    private static FrameworkElement ZoomGlyph(bool plus)
    {
        var canvas = Shape(Geo(g =>
        {
            g.BeginFigure(new Point(14.5, 15), false, false);
            g.LineTo(new Point(11, 11.5), true, false);
            g.BeginFigure(new Point(4.5, 7), false, false);
            g.LineTo(new Point(9.5, 7), true, false);
            if (plus) { g.BeginFigure(new Point(7, 4.5), false, false); g.LineTo(new Point(7, 9.5), true, false); }
        }));
        canvas.Children.Insert(0, new Ellipse
        {
            Width = 10, Height = 10, Margin = new Thickness(2, 2, 0, 0),
            Stroke = Brushes.White, StrokeThickness = 1.6,
        });
        return canvas;
    }

    // ---- primitives ----

    private static FrameworkElement TranslatePair()
    {
        var canvas = new Canvas { Width = 18, Height = 18 };
        var latin = new TextBlock
        {
            Text = "A",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
        };
        Canvas.SetLeft(latin, 0);
        Canvas.SetTop(latin, -1);
        var cjk = new TextBlock
        {
            // YaHei first so the ideograph renders with the same weight as the Latin "A" beside it.
            Text = "文",
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
        };
        Canvas.SetLeft(cjk, 7.5);
        Canvas.SetTop(cjk, 5);
        canvas.Children.Add(latin);
        canvas.Children.Add(cjk);
        return canvas;
    }

    private static TextBlock Glyph(string text, bool bold = false) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 14,
        FontWeight = bold ? FontWeights.Bold : FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Canvas Shape(Geometry geo)
    {
        var path = new Path
        {
            Data = geo,
            Stroke = Brushes.White,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var canvas = new Canvas { Width = 18, Height = 18, Children = { path } };
        return canvas;
    }

    private static Canvas Mirror(FrameworkElement element)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new ScaleTransform(-1, 1);
        return new Canvas { Width = 18, Height = 18, Children = { element } };
    }

    private static Geometry Geo(Action<System.Windows.Media.StreamGeometryContext> build)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open()) build(ctx);
        geo.Freeze();
        return geo;
    }

    private static Geometry Arrow() => Geo(g =>
    {
        g.BeginFigure(new Point(2, 16), false, false);
        g.LineTo(new Point(14, 4), true, false);
        g.LineTo(new Point(14, 9), true, false);
        g.BeginFigure(new Point(14, 4), false, false);
        g.LineTo(new Point(9, 4), true, false);
    });
}
