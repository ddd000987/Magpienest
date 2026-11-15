using System.Runtime.InteropServices;
using System.Windows;

namespace Magpienest.Plugins.ImageWindow;

/// <summary>A monitor's bounds and working area in physical pixels.</summary>
/// <param name="Bounds">Whole monitor.</param>
/// <param name="WorkArea">Monitor minus the taskbar.</param>
internal readonly record struct MonitorArea(Rect Bounds, Rect WorkArea);

/// <summary>The few Win32 calls a borderless, per-monitor-DPI window needs. Coordinates are physical pixels.</summary>
internal static class NativeMethods
{
    public const int WM_SIZING = 0x0214;

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    public static Point CursorPosition() => GetCursorPos(out var p) ? new Point(p.X, p.Y) : new Point(0, 0);

    public static MonitorArea MonitorAt(Point point) =>
        Describe(MonitorFromPoint(new POINT { X = (int)point.X, Y = (int)point.Y }, MONITOR_DEFAULTTONEAREST));

    public static MonitorArea MonitorOf(IntPtr hwnd) => Describe(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    public static Rect WindowBounds(IntPtr hwnd) => GetWindowRect(hwnd, out var r) ? ToRect(r) : Rect.Empty;

    public static void Place(IntPtr hwnd, Rect bounds) =>
        SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y),
                     (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), SWP_NOZORDER | SWP_NOACTIVATE);

    public static Rect ReadRect(IntPtr lParam) => ToRect(Marshal.PtrToStructure<RECT>(lParam));

    public static void WriteRect(IntPtr lParam, Rect rect) => Marshal.StructureToPtr(new RECT
    {
        Left = (int)Math.Round(rect.Left),
        Top = (int)Math.Round(rect.Top),
        Right = (int)Math.Round(rect.Right),
        Bottom = (int)Math.Round(rect.Bottom),
    }, lParam, false);

    private static MonitorArea Describe(IntPtr monitor)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            var fallback = new Rect(0, 0, 1920, 1080);
            return new MonitorArea(fallback, fallback);
        }

        return new MonitorArea(ToRect(info.Monitor), ToRect(info.Work));
    }

    private static Rect ToRect(RECT r) => new(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top));

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
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
