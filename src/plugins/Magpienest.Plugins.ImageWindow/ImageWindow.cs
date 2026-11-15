using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Magpienest.Sdk;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.ImageWindow;

/// <summary>
/// A frameless window that is exactly the image: drag to move, wheel to resize, edges keep the aspect ratio.
/// Positions are handled in physical pixels through <see cref="NativeMethods"/> (the app is per-monitor DPI aware).
/// </summary>
internal sealed class ImageWindow : Window
{
    private static readonly TimeSpan ToastTime = TimeSpan.FromSeconds(1.5);

    private readonly Size _image;
    private readonly BitmapSource _bitmap;
    private readonly string? _filePath;
    private readonly ILog _log;
    private readonly Border _toast;
    private readonly TextBlock _toastText;
    private readonly DispatcherTimer _toastTimer;
    private readonly Button _close;
    private Rect _initial;
    private IntPtr _hwnd;

    /// <param name="bitmap">Decoded image (may be smaller than <paramref name="pixelSize"/> for huge images).</param>
    /// <param name="pixelSize">The image's real pixel size, used for "actual size" and the aspect ratio.</param>
    /// <param name="request">The request (title and file path).</param>
    /// <param name="bounds">Initial bounds in physical pixels.</param>
    /// <param name="topmost">Start always-on-top.</param>
    /// <param name="log">Plugin log.</param>
    public ImageWindow(BitmapSource bitmap, Size pixelSize, ImageWindowRequest request, Rect bounds, bool topmost, ILog log)
    {
        _bitmap = bitmap;
        _image = pixelSize;
        _filePath = request.FilePath;
        _initial = bounds;
        _log = log;

        Title = request.Name;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = true;
        Topmost = topmost;
        Width = 200;
        Height = 200;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        // Uniform: whatever happens to the window (snap, maximise, DPI change), the picture is never stretched.
        // Normally the window already has the picture's aspect ratio (WM_SIZING), so nothing is letterboxed.
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        // Close button, shown while the mouse is over the window.
        _close = new Button
        {
            Content = new TextBlock { Text = "", FontSize = 12 },
            Width = 36,
            Height = 36,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 8, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Focusable = false,
            Visibility = Visibility.Collapsed,
            Opacity = 0.9,
            ToolTip = Loc.Get("ImageWindow.CloseTip"),
        };
        ((TextBlock)_close.Content).SetResourceReference(TextBlock.FontFamilyProperty, "SymbolThemeFontFamily");
        _close.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        _close.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        _close.Click += (_, _) => Close();
        MouseEnter += (_, _) => _close.Visibility = Visibility.Visible;
        MouseLeave += (_, _) => _close.Visibility = Visibility.Collapsed;

        _toastText = new TextBlock { Margin = new Thickness(12, 6, 12, 6) };
        _toastText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        _toast = new Border
        {
            Child = _toastText,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 16),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _toast.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        _toast.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        _toastTimer = new DispatcherTimer { Interval = ToastTime };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast.Visibility = Visibility.Collapsed;
        };

        Content = new Grid { Children = { image, _toast, _close } };

        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseRightButtonUp += OnMouseRightButtonUp;
        MouseWheel += OnMouseWheel;
        KeyDown += OnKeyDown;
        Closed += (_, _) => _toastTimer.Stop();
    }

    /// <summary>Current bounds in physical pixels (for tests).</summary>
    internal Rect PhysicalBounds => _hwnd == IntPtr.Zero ? _initial : NativeMethods.WindowBounds(_hwnd);

    /// <summary>The hover close button (for tests).</summary>
    internal Button CloseButton => _close;

    /// <summary>How the picture is fitted (for tests).</summary>
    internal Stretch ImageStretch => ((Image)((Grid)Content).Children[0]).Stretch;

    /// <summary>The text of the toast currently shown, or null (for tests).</summary>
    internal string? ToastText => _toast.Visibility == Visibility.Visible ? _toastText.Text : null;

    /// <summary>Toggles always-on-top and says so.</summary>
    internal void ToggleTopmost()
    {
        Topmost = !Topmost;
        ShowToast(Loc.Get(Topmost ? "ImageWindow.Toast.TopmostOn" : "ImageWindow.Toast.TopmostOff"));
    }

    /// <summary>One image pixel per screen pixel, keeping the window's centre.</summary>
    internal void ActualSize() => Place(WindowGeometry.CentredOn(PhysicalBounds, WindowGeometry.Clamp(_image, _image)));

    /// <summary>Fits 80 % of the working area of the window's monitor, keeping the centre.</summary>
    internal void FitToScreen()
    {
        var work = (_hwnd == IntPtr.Zero ? NativeMethods.MonitorAt(_initial.Location) : NativeMethods.MonitorOf(_hwnd)).WorkArea;
        Place(WindowGeometry.KeepInside(WindowGeometry.CentredOn(PhysicalBounds, WindowGeometry.FitSize(_image, work)), work));
    }

    /// <summary>Scales the window around <paramref name="cursor"/> (physical pixels).</summary>
    internal void ScaleBy(double factor, Point cursor) =>
        Place(WindowGeometry.ScaleAround(PhysicalBounds, cursor, factor, _image));

    private void Place(Rect bounds)
    {
        if (_hwnd == IntPtr.Zero)
        {
            _initial = bounds;
            return;
        }

        NativeMethods.Place(_hwnd, bounds);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        NativeMethods.Place(_hwnd, _initial);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_SIZING)
        {
            var proposed = NativeMethods.ReadRect(lParam);
            NativeMethods.WriteRect(lParam, WindowGeometry.ConstrainSizing(proposed, wParam.ToInt32(), _image));
            handled = true;
            return new IntPtr(1);
        }

        return IntPtr.Zero;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Close();
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was already released.
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScaleBy(Math.Pow(1.1, e.Delta / 120.0), NativeMethods.CursorPosition());
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.T when ctrl:
                ToggleTopmost();
                break;
            case Key.C when ctrl:
                Copy();
                break;
            case Key.D1 or Key.NumPad1 when !ctrl:
                ActualSize();
                break;
            case Key.D0 or Key.NumPad0 when !ctrl:
                FitToScreen();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = BuildMenu();
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Built each time it opens, so it always speaks the current language.</summary>
    internal ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("ImageWindow.Menu.Topmost", "ImageWindow.Gesture.Topmost", ToggleTopmost, isChecked: Topmost));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("ImageWindow.Menu.ActualSize", "ImageWindow.Gesture.ActualSize", ActualSize));
        menu.Items.Add(Item("ImageWindow.Menu.Fit", "ImageWindow.Gesture.Fit", FitToScreen));
        menu.Items.Add(Item("ImageWindow.Menu.Copy", "ImageWindow.Gesture.Copy", Copy));
        if (_filePath is not null)
        {
            menu.Items.Add(Item("ImageWindow.Menu.ShowInExplorer", null, ShowInExplorer));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("ImageWindow.Menu.Close", "ImageWindow.Gesture.Close", Close));
        return menu;
    }

    private static MenuItem Item(string key, string? gestureKey, Action action, bool? isChecked = null)
    {
        var item = new MenuItem
        {
            Header = Loc.Get(key),
            InputGestureText = gestureKey is null ? string.Empty : Loc.Get(gestureKey),
        };
        if (isChecked is { } value)
        {
            item.IsCheckable = true;
            item.IsChecked = value;
        }

        item.Click += (_, _) => action();
        return item;
    }

    private void Copy()
    {
        try
        {
            Clipboard.SetImage(_bitmap);
            ShowToast(Loc.Get("ImageWindow.Toast.Copied"));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _log.Warn("Copying the image to the clipboard failed.", ex);
        }
    }

    private void ShowInExplorer()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_filePath}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _log.Warn("Opening Explorer failed.", ex);
        }
    }

    private void ShowToast(string text)
    {
        _toastText.Text = text;
        _toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
