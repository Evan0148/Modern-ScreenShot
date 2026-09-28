using ModernScreenShot.App.Interop;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.App.Capture;

public sealed record MonitorInfo(IntPtr Handle, PixelRect Bounds, PixelRect WorkArea, uint DpiX, uint DpiY, bool IsPrimary, string DeviceName)
{
    public double Scale => DpiX / 96.0;
}

/// <summary>Monitor topology in physical pixels. Virtual-screen coordinates may be negative.</summary>
public sealed class MonitorService
{
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = Describe(h);
            if (info is not null) list.Add(info);
            return true;
        }, IntPtr.Zero);
        // primary first, then left-to-right
        return [.. list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y)];
    }

    public PixelRect GetVirtualScreen() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    public MonitorInfo FromPoint(int x, int y)
    {
        var h = NativeMethods.MonitorFromPoint(new POINT(x, y), NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (Describe(h) is { } info) return info;
        var monitors = GetMonitors();
        return monitors.Count > 0
            ? monitors[0]
            : throw new InvalidOperationException("No display monitors were detected.");
    }

    public MonitorInfo GetCursorMonitor()
    {
        NativeMethods.GetCursorPos(out var p);
        return FromPoint(p.X, p.Y);
    }

    public MonitorInfo? FromWindow(IntPtr hwnd)
    {
        var h = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return Describe(h);
    }

    private static MonitorInfo? Describe(IntPtr hMonitor)
    {
        if (hMonitor == IntPtr.Zero) return null;
        var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
        if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi)) return null;
        uint dx = 96, dy = 96;
        if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out var x, out var y) == 0 && x > 0)
        {
            dx = x; dy = y;
        }
        return new MonitorInfo(hMonitor, mi.rcMonitor.ToPixelRect(), mi.rcWork.ToPixelRect(), dx, dy,
            (mi.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0, mi.szDevice ?? "");
    }
}
