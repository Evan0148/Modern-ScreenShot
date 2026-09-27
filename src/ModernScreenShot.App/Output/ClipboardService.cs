using System.Runtime.InteropServices;
using System.Windows;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.App.Output;

/// <summary>Puts images on the clipboard with retries (clipboards are briefly locked by other apps).</summary>
public sealed class ClipboardService
{
    /// <summary>Places a bitmap image on the clipboard. T7 adds an explicit PNG + CF_DIBV5 payload.</summary>
    public bool TryPutImage(PixelBuffer image)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetImage(image.ToBitmapSource());
                return true;
            }
            catch (ExternalException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
            catch (ExternalException ex)
            {
                Log.Error("Clipboard rejected the image after retries", ex);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("Clipboard copy failed", ex);
                return false;
            }
        }
    }
}
