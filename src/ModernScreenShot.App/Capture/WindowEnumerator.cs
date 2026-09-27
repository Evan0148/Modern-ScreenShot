using ModernScreenShot.App.Interop;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.App.Capture;

/// <summary>
/// Enumerates windows for snapping and capture. All rectangles are physical pixels.
/// Call <see cref="Refresh"/> before showing any overlay, then use <see cref="HitTest"/> against that snapshot.
/// </summary>
public sealed class WindowEnumerator
{
    private static readonly HashSet<string> DesktopClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW" };
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Windows.UI.Core.CoreWindow", // hidden UWP hosts / start menu remnants
        "Shell_InputSwitchTopLevelWindow",
    };

    private readonly object _gate = new();
    private readonly int _ownProcessId = Environment.ProcessId;
    private List<WindowInfo> _topLevel = [];
    private List<WindowInfo> _desktop = [];
    private readonly Dictionary<IntPtr, List<(WindowInfo Info, int Depth)>> _children = [];

    /// <summary>Visible top-level windows in z-order (topmost first), excluding this process and the desktop shell.</summary>
    public IReadOnlyList<WindowInfo> GetTopLevelWindows(bool includeDesktop = false)
    {
        var (windows, desktop) = EnumerateTopLevel();
        if (includeDesktop) windows.AddRange(desktop);
        return windows;
    }

    /// <summary>Visible descendants of <paramref name="hwnd"/> (all depths), clipped to the parent's bounds.</summary>
    public IReadOnlyList<WindowInfo> GetChildWindows(IntPtr hwnd) => [.. EnumerateChildren(hwnd).Select(c => c.Info)];

    /// <summary>Takes a fresh snapshot used by <see cref="HitTest"/>. Children are enumerated lazily per window.</summary>
    public void Refresh()
    {
        var (windows, desktop) = EnumerateTopLevel();
        lock (_gate)
        {
            _topLevel = windows;
            _desktop = desktop;
            _children.Clear();
        }
    }

    public IReadOnlyList<WindowInfo> Snapshot
    {
        get { lock (_gate) return [.. _topLevel]; }
    }

    /// <summary>
    /// Returns the deepest window under (x, y) from the last snapshot. Falls back to the desktop window if nothing else matches.
    /// </summary>
    public WindowInfo? HitTest(int x, int y, bool includeChildren)
    {
        WindowInfo? top;
        lock (_gate)
        {
            top = _topLevel.FirstOrDefault(w => w.Bounds.Contains(x, y)) ?? _desktop.FirstOrDefault(w => w.Bounds.Contains(x, y));
        }
        if (top is null || !includeChildren) return top;

        List<(WindowInfo Info, int Depth)> children;
        lock (_gate)
        {
            if (!_children.TryGetValue(top.Handle, out children!))
            {
                children = EnumerateChildren(top.Handle);
                _children[top.Handle] = children;
            }
        }

        WindowInfo? best = null;
        int bestDepth = -1;
        long bestArea = long.MaxValue;
        foreach (var (info, depth) in children)
        {
            if (!info.Bounds.Contains(x, y)) continue;
            long area = (long)info.Bounds.Width * info.Bounds.Height;
            if (depth > bestDepth || (depth == bestDepth && area < bestArea))
            {
                best = info; bestDepth = depth; bestArea = area;
            }
        }
        return best ?? top;
    }

    private (List<WindowInfo> Windows, List<WindowInfo> Desktop) EnumerateTopLevel()
    {
        var windows = new List<WindowInfo>();
        var desktop = new List<WindowInfo>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            var info = DescribeTopLevel(hwnd, out bool isDesktop);
            if (info is not null) (isDesktop ? desktop : windows).Add(info);
            return true;
        }, IntPtr.Zero);
        return (windows, desktop);
    }

    private WindowInfo? DescribeTopLevel(IntPtr hwnd, out bool isDesktop)
    {
        isDesktop = false;
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd) || NativeMethods.IsCloaked(hwnd)) return null;
        NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
        if (pid == _ownProcessId) return null;

        string cls = NativeMethods.GetWindowClass(hwnd);
        if (IgnoredClasses.Contains(cls)) return null;
        string title = NativeMethods.GetWindowTitle(hwnd);
        long ex = NativeMethods.GetExStyle(hwnd);
        if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0 && title.Length == 0) return null;

        var bounds = NativeMethods.GetFrameBounds(hwnd).ToPixelRect();
        if (bounds.IsEmpty) return null;

        isDesktop = DesktopClasses.Contains(cls);
        return new WindowInfo(hwnd, title, cls, bounds, pid, false);
    }

    private static List<(WindowInfo Info, int Depth)> EnumerateChildren(IntPtr parent)
    {
        var result = new List<(WindowInfo, int)>();
        var parentBounds = NativeMethods.GetFrameBounds(parent).ToPixelRect();
        NativeMethods.GetWindowThreadProcessId(parent, out int pid);
        NativeMethods.EnumChildWindows(parent, (hwnd, _) =>
        {
            if (!IsVisibleChain(hwnd, parent)) return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return true;
            var bounds = r.ToPixelRect().Intersect(parentBounds);
            if (bounds.IsEmpty || bounds.Width < 4 || bounds.Height < 4) return true;
            int depth = Depth(hwnd, parent);
            result.Add((new WindowInfo(hwnd, NativeMethods.GetWindowTitle(hwnd), NativeMethods.GetWindowClass(hwnd), bounds, pid, true), depth));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsVisibleChain(IntPtr hwnd, IntPtr root)
    {
        // IsWindowVisible already requires all ancestors visible; guard cloaked parents too.
        return NativeMethods.IsWindowVisible(hwnd) && hwnd != root;
    }

    private static int Depth(IntPtr hwnd, IntPtr root)
    {
        int depth = 0;
        var cur = hwnd;
        while (cur != IntPtr.Zero && cur != root && depth < 64)
        {
            cur = NativeMethods.GetAncestor(cur, NativeMethods.GA_PARENT);
            depth++;
        }
        return depth;
    }
}
