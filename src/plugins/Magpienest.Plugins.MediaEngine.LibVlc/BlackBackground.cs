using System.Runtime.InteropServices;

namespace Magpienest.Plugins.MediaEngine.LibVlc;

/// <summary>
/// Paints the video host window black. LibVLCSharp hosts the video in a plain "static" child window that LibVLC
/// draws only where the picture is; the rest (the bars beside a narrow video) was never painted and showed stale
/// pixels of earlier videos or of the page behind. Subclassing the window fills everything outside LibVLC's own
/// child window with black. UI thread only.
/// </summary>
internal sealed class BlackBackground : IDisposable
{
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmPaint = 0x000F;
    private const int GwlStyle = -16;
    private const long WsClipChildren = 0x02000000;
    private const int BlackBrush = 4;

    private static readonly UIntPtr SubclassId = new(0x4D61_6770);   // "Magp"

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc;   // kept alive while subclassed
    private bool _disposed;

    private BlackBackground(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _proc = WndProc;
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    /// <summary>Starts painting <paramref name="hwnd"/> black; null when it cannot be subclassed.</summary>
    public static BlackBackground? Attach(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        var background = new BlackBackground(hwnd);
        if (!SetWindowSubclass(hwnd, background._proc, SubclassId, UIntPtr.Zero))
        {
            return null;
        }

        // Children (LibVLC's video window) keep their pixels; only the uncovered part is filled.
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style | WsClipChildren));
        background.Repaint();
        return background;
    }

    /// <summary>Repaints the uncovered area (after the picture size changed).</summary>
    public void Repaint()
    {
        if (!_disposed)
        {
            InvalidateRect(_hwnd, IntPtr.Zero, true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveWindowSubclass(_hwnd, _proc, SubclassId);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        switch (msg)
        {
            case WmEraseBkgnd:
                return new IntPtr(1);
            case WmPaint:
                var dc = BeginPaint(hwnd, out var paint);
                if (dc != IntPtr.Zero)
                {
                    FillRect(dc, ref paint.Paint, GetStockObject(BlackBrush));
                }

                EndPaint(hwnd, ref paint);
                return IntPtr.Zero;
            default:
                return DefSubclassProc(hwnd, msg, wParam, lParam);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc;
        public int Erase;
        public Rect Paint;
        public int Restore;
        public int IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Reserved;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr dc, ref Rect rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
