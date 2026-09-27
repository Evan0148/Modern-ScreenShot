using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.App.Capture;

/// <summary>A top-level or child window snapshot. Bounds are DWM extended frame bounds in physical pixels.</summary>
public sealed record WindowInfo(IntPtr Handle, string Title, string ClassName, PixelRect Bounds, int ProcessId, bool IsChild);
