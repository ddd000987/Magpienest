using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Magpienest.Plugins.ImageViewer;

/// <summary>The viewer overlay: renders the current page through <see cref="ViewTransformModel"/> and maps input.</summary>
internal sealed partial class ViewerView : UserControl
{
    private static readonly TimeSpan ChromeTimeout = TimeSpan.FromSeconds(2.5);

    private readonly ViewerViewModel _vm;
    private readonly ViewTransformModel _transform = new();
    private readonly DispatcherTimer _chromeTimer;
    private Point? _dragStart;
    private bool _chromeHidden;
    private Point _lastMouse = new(double.NaN, double.NaN);

    public ViewerView(ViewerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        _transform.StretchSmall = vm.Settings.StretchSmall;
        _chromeTimer = new DispatcherTimer { Interval = ChromeTimeout };
        _chromeTimer.Tick += (_, _) => HideChrome();
        vm.PageChanged += OnPageChanged;
        vm.PropertyChanged += OnViewModelChanged;
        vm.Closed += () => _chromeTimer.Stop();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewMouseDown += OnPreviewMouseDown;
        Loaded += (_, _) =>
        {
            Keyboard.Focus(this);
            ShowChrome();
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewerViewModel.IsFullScreen))
        {
            FullScreenGlyph.Text = _vm.IsFullScreen ? "" : "";
        }
    }

    private void OnPageChanged()
    {
        if (_vm.Current is { Failed: false } page && page.PixelWidth > 0 && page.PixelHeight > 0)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _transform.SetContent(new Size(page.PixelWidth / dpi.DpiScaleX, page.PixelHeight / dpi.DpiScaleY));
        }
        else
        {
            _transform.SetContent(Size.Empty);
        }

        Apply();
    }

    /// <summary>Pushes the model into the image transforms.</summary>
    private void Apply()
    {
        var content = _transform.Content;
        if (!_transform.HasContent)
        {
            PageImage.Width = 0;
            PageImage.Height = 0;
            return;
        }

        // The image is laid out unrotated; rotate around its centre, then move the rotated bounds to the origin.
        var rotated = _transform.Rotation % 180 != 0;
        var width = rotated ? content.Height : content.Width;
        var height = rotated ? content.Width : content.Height;
        PageImage.Width = width;
        PageImage.Height = height;
        RotateXf.Angle = _transform.Rotation;
        RotateXf.CenterX = width / 2;
        RotateXf.CenterY = height / 2;
        RecentreXf.X = rotated ? (height - width) / 2 : 0;
        RecentreXf.Y = rotated ? (width - height) / 2 : 0;
        ScaleXf.ScaleX = _transform.Scale;
        ScaleXf.ScaleY = _transform.Scale;
        OffsetXf.X = _transform.Offset.X;
        OffsetXf.Y = _transform.Offset.Y;

        _vm.ZoomPercent = _transform.Scale * 100;
        UpdateCursor();
    }

    private void UpdateCursor() =>
        Stage.Cursor = _chromeHidden ? Cursors.None
            : _dragStart is not null ? Cursors.SizeAll
            : _transform.CanPan ? Cursors.Hand
            : null;

    // ---- keyboard ------------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var handled = true;
        switch (e.Key)
        {
            case Key.Right or Key.PageDown or Key.Space or Key.Down when !ctrl:
                _vm.Next();
                break;
            case Key.Left or Key.PageUp or Key.Back or Key.Up when !ctrl:
                _vm.Previous();
                break;
            case Key.Home:
                _vm.First();
                break;
            case Key.End:
                _vm.Last();
                break;
            case Key.F11 or Key.F:
                _vm.ToggleFullScreen();
                break;
            case Key.Escape:
                _ = _vm.EscapeAsync();
                break;
            case Key.I:
                _vm.IsInfoVisible = !_vm.IsInfoVisible;
                break;
            case Key.OemPlus or Key.Add:
                _transform.ZoomBy(ViewTransformModel.Step);
                Apply();
                break;
            case Key.OemMinus or Key.Subtract:
                _transform.ZoomBy(1 / ViewTransformModel.Step);
                Apply();
                break;
            case Key.D0 or Key.NumPad0:
                _transform.Fit();
                Apply();
                break;
            case Key.D1 or Key.NumPad1:
                _transform.ActualSize();
                Apply();
                break;
            case Key.R:
                _transform.Rotate();
                Apply();
                break;
            default:
                handled = false;
                break;
        }

        e.Handled = handled;
    }

    // ---- mouse ---------------------------------------------------------------------------------

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _transform.ZoomBy(Math.Pow(ViewTransformModel.Step, e.Delta / 120.0), e.GetPosition(Stage));
            Apply();
        }
        else if (e.Delta < 0)
        {
            _vm.Next();
        }
        else if (e.Delta > 0)
        {
            _vm.Previous();
        }

        e.Handled = true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
        {
            _ = _vm.CloseAsync();
            e.Handled = true;
        }
    }

    private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.Focus(this);
        if (e.ClickCount == 2)
        {
            _transform.ToggleFitActual(e.GetPosition(Stage));
            Apply();
            e.Handled = true;
            return;
        }

        if (_transform.CanPan)
        {
            _dragStart = e.GetPosition(Stage);
            Stage.CaptureMouse();
            Apply();
            e.Handled = true;
        }
    }

    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(Stage);
        if (position != _lastMouse)
        {
            _lastMouse = position;
            ShowChrome();
        }

        if (_dragStart is { } start && e.LeftButton == MouseButtonState.Pressed)
        {
            _transform.Pan(position - start);
            _dragStart = position;
            Apply();
        }
    }

    private void Stage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not null)
        {
            Stage.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void Stage_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _dragStart = null;
        Apply();
    }

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _transform.SetViewport(e.NewSize);
        Apply();
    }

    // ---- chrome --------------------------------------------------------------------------------

    private void ShowChrome()
    {
        TopBar.Visibility = Visibility.Visible;
        if (_chromeHidden)
        {
            _chromeHidden = false;
            UpdateCursor();
        }

        _chromeTimer.Stop();
        _chromeTimer.Start();
    }

    private void HideChrome()
    {
        _chromeTimer.Stop();
        if (TopBar.IsMouseOver || _dragStart is not null)
        {
            _chromeTimer.Start();
            return;
        }

        TopBar.Visibility = Visibility.Collapsed;
        _chromeHidden = true;
        UpdateCursor();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _ = _vm.CloseAsync();

    private void FullScreen_Click(object sender, RoutedEventArgs e) => _vm.ToggleFullScreen();

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        _transform.Fit();
        Apply();
    }

    private void Actual_Click(object sender, RoutedEventArgs e)
    {
        _transform.ActualSize();
        Apply();
    }
}
