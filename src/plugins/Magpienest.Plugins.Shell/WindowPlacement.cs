using System.Runtime.InteropServices;

namespace Magpienest.Plugins.Shell;

/// <summary>
/// The main window's position as Windows stores it (config key "placement"): the normal (not maximized) bounds in
/// physical pixels, and whether it was maximized. Windows' own GetWindowPlacement/SetWindowPlacement handle monitors
/// with different scaling, which WPF's Left/Top/Width (device-independent units of whichever monitor the window was
/// on) do not: restoring those on a monitor with another scale changed the size at every start.
/// </summary>
internal sealed class SavedPlacement
{
    public int Left { get; set; }

    public int Top { get; set; }

    public int Right { get; set; }

    public int Bottom { get; set; }

    public bool Maximized { get; set; }
}

/// <summary>The Win32 calls behind <see cref="SavedPlacement"/>.</summary>
internal static class WindowPlacement
{
    private const int SW_HIDE = 0;
    private const int SW_SHOWMAXIMIZED = 3;
    private const uint MONITOR_DEFAULTTONULL = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>Reads the window's normal bounds; null when the window has no handle or Windows refuses.</summary>
    /// <param name="hwnd">The window.</param>
    /// <param name="maximized">Stored as is (the caller knows about full screen).</param>
    public static SavedPlacement? Read(IntPtr hwnd, bool maximized)
    {
        var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (hwnd == IntPtr.Zero || !GetWindowPlacement(hwnd, ref placement))
        {
            return null;
        }

        var r = placement.NormalPosition;
        return new SavedPlacement { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom, Maximized = maximized };
    }

    /// <summary>True when the saved bounds are a real rectangle that is at least partly on a connected monitor.</summary>
    public static bool IsUsable(SavedPlacement saved)
    {
        if (saved.Right - saved.Left < 100 || saved.Bottom - saved.Top < 100)
        {
            return false;
        }

        var rect = new RECT { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom };
        return MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL) != IntPtr.Zero;
    }

    /// <summary>
    /// Moves the not yet visible window to the saved normal bounds; it stays hidden. Call from
    /// <c>SourceInitialized</c>, before the window is shown; maximizing is left to WPF's <c>WindowState</c>.
    /// </summary>
    public static bool Apply(IntPtr hwnd, SavedPlacement saved)
    {
        var placement = new WINDOWPLACEMENT
        {
            Length = Marshal.SizeOf<WINDOWPLACEMENT>(),
            ShowCmd = SW_HIDE,
            NormalPosition = new RECT { Left = saved.Left, Top = saved.Top, Right = saved.Right, Bottom = saved.Bottom },
            MinPosition = new POINT { X = -1, Y = -1 },
            MaxPosition = new POINT { X = -1, Y = -1 },
        };

        // Twice: when the target monitor has another scale, the first call moves the window there and Windows
        // rescales it on the way; the second sets the exact size at the new scale.
        return hwnd != IntPtr.Zero && SetWindowPlacement(hwnd, ref placement) && SetWindowPlacement(hwnd, ref placement);
    }

    /// <summary>
    /// First start: <paramref name="width"/> × <paramref name="height"/> device-independent units, scaled for the
    /// monitor under the mouse pointer and centered in its working area (at most 90 % of it).
    /// </summary>
    public static SavedPlacement DefaultOnCursorMonitor(double width, double height)
    {
        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return new SavedPlacement { Left = 100, Top = 100, Right = 100 + (int)width, Bottom = 100 + (int)height };
        }

        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
        var work = info.Work;
        var areaWidth = work.Right - work.Left;
        var areaHeight = work.Bottom - work.Top;
        var w = (int)Math.Min(Math.Round(width * scale), areaWidth * 0.9);
        var h = (int)Math.Min(Math.Round(height * scale), areaHeight * 0.9);
        var left = work.Left + ((areaWidth - w) / 2);
        var top = work.Top + ((areaHeight - h) / 2);
        return new SavedPlacement { Left = left, Top = top, Right = left + w, Bottom = top + h };
    }

    /// <summary>True when Windows reports the window as maximized.</summary>
    public static bool IsMaximized(IntPtr hwnd)
    {
        var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        return hwnd != IntPtr.Zero && GetWindowPlacement(hwnd, ref placement) && placement.ShowCmd == SW_SHOWMAXIMIZED;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public POINT MinPosition;
        public POINT MaxPosition;
        public RECT NormalPosition;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }
}
