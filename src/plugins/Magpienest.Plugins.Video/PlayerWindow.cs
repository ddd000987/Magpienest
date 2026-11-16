using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Video;

/// <summary>
/// A frameless window that plays one video (and its playlist); the controls appear while the mouse is over it.
/// Drag the picture to move it, drag an edge to resize; closing the window stops playback.
/// </summary>
internal sealed class PlayerWindow : Window, IPlayerHost
{
    /// <summary>Black edge around the picture where the window can be resized (the video surface covers the rest).</summary>
    internal const double ResizeFrame = 5;

    /// <summary>The first size takes at most this share of the work area.</summary>
    internal const double MaxScreenShare = 0.75;

    internal const double MinVideoWidth = 360;
    internal const double MinVideoHeight = 200;

    private readonly PlayerView _view;
    private readonly FrameworkElement _video;
    private readonly Func<LibraryItem, Task<(int Width, int Height)?>> _sizeOf;
    private WindowState _restoreState = WindowState.Normal;
    private bool _fullScreen;

    /// <param name="session">What to play.</param>
    /// <param name="videoSize">The first video's size in pixels, when known.</param>
    /// <param name="sizeOf">Finds a video's size (for the next videos of the playlist).</param>
    /// <param name="settings">Player settings.</param>
    /// <param name="shell">Shell (icon, notifications).</param>
    public PlayerWindow(PlayerSession session, (int Width, int Height)? videoSize,
                        Func<LibraryItem, Task<(int Width, int Height)?>> sizeOf, VideoSettings settings, IShellService shell)
    {
        Session = session;
        _sizeOf = sizeOf;
        var size = InitialSize(videoSize?.Width ?? 0, videoSize?.Height ?? 0, SystemParameters.WorkArea.Size);
        Width = size.Width;
        Height = size.Height;
        MinWidth = MinVideoWidth + 2 * ResizeFrame;
        MinHeight = MinVideoHeight + 2 * ResizeFrame;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(ResizeFrame),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        Background = Brushes.Black;   // video letterbox; same in light and dark themes
        Icon = shell.MainWindow.Icon;
        Title = session.Current.Title;

        // The app theme gives windows a see-through backdrop (Mica). On it, the pure black LibVLC paints beside
        // the picture counts as transparent and the backdrop showed through as grey patches. Keep it opaque.
        SourceInitialized += (_, _) => Opaque.Apply(this);
        Loaded += (_, _) => Opaque.Apply(this);
        Activated += (_, _) => Opaque.Apply(this);   // the theme may switch while the window is open

        _view = new PlayerView(session, settings, shell, this);
        _video = session.Player.CreateView(_view);
        _video.Margin = new Thickness(ResizeFrame);
        Content = new System.Windows.Controls.Grid { Children = { _video } };
        _view.CloseRequested += (_, _) => Close();
        session.CurrentChanged += (_, _) =>
        {
            Title = session.Current.Title;
            _ = FitToAsync(session.Current);
        };
        Closed += (_, _) =>
        {
            _view.Shutdown();
            Session.Dispose();
        };
    }

    /// <summary>What this window plays.</summary>
    public PlayerSession Session { get; }

    /// <inheritdoc />
    public bool IsFullScreen
    {
        get => _fullScreen;
        set
        {
            if (value == _fullScreen)
            {
                return;
            }

            _fullScreen = value;
            if (value)
            {
                _restoreState = WindowState;
                ResizeMode = ResizeMode.NoResize;
                _video.Margin = new Thickness(0);
                WindowState = WindowState.Normal;   // re-maximise so the taskbar is covered
                WindowState = WindowState.Maximized;
            }
            else
            {
                ResizeMode = ResizeMode.CanResize;
                _video.Margin = new Thickness(ResizeFrame);
                WindowState = _restoreState;
            }

            _view.OnHostChanged();
        }
    }

    /// <inheritdoc />
    public void BeginMove()
    {
        if (_fullScreen)
        {
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

    /// <summary>
    /// The first window size: the video's own size, made smaller to fit the screen or larger to fit the controls,
    /// keeping its shape (16:9 while the size is unknown), plus the resize edge.
    /// </summary>
    internal static Size InitialSize(int videoWidth, int videoHeight, Size workArea)
    {
        double width = videoWidth > 0 && videoHeight > 0 ? videoWidth : 1280;
        double height = videoWidth > 0 && videoHeight > 0 ? videoHeight : 720;
        var shrink = Math.Min(1, Math.Min(workArea.Width * MaxScreenShare / width, workArea.Height * MaxScreenShare / height));
        width *= shrink;
        height *= shrink;
        var grow = Math.Max(1, Math.Max(MinVideoWidth / width, MinVideoHeight / height));
        width *= grow;
        height *= grow;
        return new Size(Math.Round(width + 2 * ResizeFrame), Math.Round(height + 2 * ResizeFrame));
    }

    /// <summary>The next video of the playlist has another shape: resize around the window's centre.</summary>
    private async Task FitToAsync(LibraryItem item)
    {
        var size = await _sizeOf(item);
        if (size is not { } video || _fullScreen || WindowState != WindowState.Normal || !IsLoaded ||
            Session.Current.Id != item.Id)
        {
            return;
        }

        var target = InitialSize(video.Width, video.Height, SystemParameters.WorkArea.Size);
        var centreX = Left + ActualWidth / 2;
        var centreY = Top + ActualHeight / 2;
        Width = target.Width;
        Height = target.Height;
        Left = centreX - target.Width / 2;
        Top = centreY - target.Height / 2;
    }

    /// <summary>Turns off the theme's see-through backdrop for one window.</summary>
    private static class Opaque
    {
        private const int DwmwaSystemBackdropType = 38;
        private const int DwmsbtNone = 1;

        public static void Apply(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var none = DwmsbtNone;
            _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref none, sizeof(int));
            var margins = default(Margins);
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins
        {
            public int Left;
            public int Right;
            public int Top;
            public int Bottom;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    }
}
