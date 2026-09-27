namespace ModernScreenShot.Core.Imaging;

public static class EffectPipeline
{
    /// <summary>
    /// rounded corners + border → shadow (from image only) → reflection placed below the image
    /// (not shadowed) → padding/background.
    /// </summary>
    public static PixelBuffer Compose(PixelBuffer src, EffectSettings s)
    {
        if (!s.Enabled) return src.Clone();

        var img = s.Frame.CornerRadius >= 0.5 ? FrameEffect.RoundCorners(src, s.Frame.CornerRadius) : src.Clone();
        if (s.Frame.BorderThickness > 0 && PixelColor.TryParseHex(s.Frame.BorderColor, out var bc))
            FrameEffect.DrawInnerBorder(img, s.Frame.CornerRadius, s.Frame.BorderThickness, bc);

        var reflection = ReflectionEffect.RenderReflectionOnly(img, s.Reflection);
        int gap = reflection is null ? 0 : Math.Max(0, (int)Math.Round(s.Reflection.Gap));

        var shadow = ShadowEffect.RenderShadowOnly(img, s.Shadow, out int ox, out int oy);
        PixelBuffer composed;
        if (shadow is null && reflection is null)
        {
            composed = img;
        }
        else
        {
            int shW = shadow?.Width ?? img.Width, shH = shadow?.Height ?? img.Height;
            int reflBottom = reflection is null ? 0 : oy + img.Height + gap + reflection.Height;
            int w = shW, h = Math.Max(shH, reflBottom);
            composed = new PixelBuffer(w, h);
            if (shadow is not null) composed.Blit(shadow, 0, 0);
            if (reflection is not null) composed.DrawOver(reflection, ox, oy + img.Height + gap);
            composed.DrawOver(img, ox, oy);
        }

        return s.Frame.Padding >= 0.5 || s.Frame.Background != BackgroundKind.None
            ? FrameEffect.ApplyBackground(composed, s.Frame)
            : composed;
    }
}
