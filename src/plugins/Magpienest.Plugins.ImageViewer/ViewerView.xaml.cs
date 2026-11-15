using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Magpienest.Sdk.Localization;

namespace Magpienest.Plugins.ImageViewer;

/// <summary>The viewer overlay: renders the current frame through <see cref="ViewTransformModel"/> and maps input.</summary>
internal sealed partial class ViewerView : UserControl
{
    private static readonly TimeSpan ChromeTimeout = TimeSpan.FromSeconds(2.5);

    /// <summary>Width / height of the space reserved for a page that is not shown yet.</summary>
    private const double PlaceholderAspect = 0.7;

    /// <summary>Mouse travel (DIP) below which a press-release counts as a click, not a drag.</summary>
    private const double ClickSlop = 4;

    private readonly ViewerViewModel _vm;
    private readonly ViewTransformModel _transform = new();
    private readonly StripPresenter _strip;
    private readonly DispatcherTimer _chromeTimer;
    private Point? _dragStart;
    private Point? _pressAt;
    private int _pressClicks;
    private bool _dragged;
    private bool _chromeHidden;
    private bool _sliderDragging;
    private bool _updatingSlider;
    private Point _lastMouse = new(double.NaN, double.NaN);

    public ViewerView(ViewerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        _strip = new StripPresenter(Stage, StripCanvas, vm);
        _transform.StretchSmall = vm.Settings.StretchSmall;
        _chromeTimer = new DispatcherTimer { Interval = ChromeTimeout };
        _chromeTimer.Tick += (_, _) => HideChrome();
        vm.PageChanged += OnPageChanged;
        vm.PropertyChanged += OnViewModelChanged;
        vm.Closed += () => _chromeTimer.Stop();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewMouseDown += OnPreviewMouseDown;
        Stage.MouseRightButtonUp += OnStageRightButtonUp;

        // Every mode has the bottom bar, so the first, last or any page is reachable without keys.
        InfoLine.Margin = new Thickness(16, 16, 16, 72);
        PageSlider.Maximum = Math.Max(0, vm.Count - 1);
        PageSlider.IsDirectionReversed = vm.RightToLeft;
        PageSlider.ValueChanged += OnSliderValueChanged;
        PageSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => BeginSliderDrag()));
        PageSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => EndSliderDrag()));

        Loaded += (_, _) =>
        {
            Keyboard.Focus(this);
            ShowChrome();
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewerViewModel.IsFullScreen):
                FullScreenGlyph.Text = _vm.IsFullScreen ? "" : "";
                break;
            case nameof(ViewerViewModel.Frame):
                SyncSlider();
                break;
            case nameof(ViewerViewModel.Book):
                PageSlider.IsDirectionReversed = _vm.RightToLeft;
                break;
            case nameof(ViewerViewModel.Strip):
                ApplyFlow();
                break;
        }
    }

    // ---- layout --------------------------------------------------------------------------------

    /// <summary>Lays the frame's pages out edge to edge at a common height and resets the transform to Fit.</summary>
    private void OnPageChanged()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        Size SizeOf(PageEntry? page) => page is { Failed: false, PixelWidth: > 0, PixelHeight: > 0 }
            ? new Size(page.PixelWidth / dpi.DpiScaleX, page.PixelHeight / dpi.DpiScaleY)
            : Size.Empty;

        var first = SizeOf(_vm.Current);
        if (!_vm.IsSpread)
        {
            PageImage2.Visibility = Visibility.Collapsed;
            if (first.IsEmpty)
            {
                _transform.SetContent(Size.Empty);
            }
            else
            {
                Place(PageImage, 0, first.Width, first.Height);
                _transform.SetContent(first);
            }

            Apply();
            return;
        }

        var second = SizeOf(_vm.Second);
        if (first.IsEmpty && second.IsEmpty && _vm.Current is null && _vm.Second is null)
        {
            PageImage2.Visibility = Visibility.Collapsed;
            _transform.SetContent(Size.Empty);
            Apply();
            return;
        }

        // Common height: the taller page's; a page without a size reserves a portrait placeholder.
        var height = Math.Max(first.IsEmpty ? 0 : first.Height, second.IsEmpty ? 0 : second.Height);
        if (height <= 0)
        {
            height = 1000 / dpi.DpiScaleY;
        }

        var firstWidth = first.IsEmpty ? height * PlaceholderAspect : first.Width * height / first.Height;
        var secondWidth = second.IsEmpty ? height * PlaceholderAspect : second.Width * height / second.Height;

        // Right-to-left: the lower page index is on the right.
        var rtl = _vm.RightToLeft;
        Place(PageImage, rtl ? secondWidth : 0, firstWidth, height);
        Place(PageImage2, rtl ? 0 : firstWidth, secondWidth, height);
        PageImage2.Visibility = Visibility.Visible;
        _transform.SetContent(new Size(firstWidth + secondWidth, height));
        Apply();
    }

    private static void Place(Image image, double left, double width, double height)
    {
        Canvas.SetLeft(image, left);
        Canvas.SetTop(image, 0);
        image.Width = width;
        image.Height = height;
    }

    /// <summary>The strip presenter for tests.</summary>
    internal StripPresenter StripView => _strip;

    /// <summary>Paged ↔ strip: show one renderer and hide the other.</summary>
    private void ApplyFlow()
    {
        if (_vm.Strip is not null)
        {
            Spread.Visibility = Visibility.Collapsed;
            _strip.Enter(_vm.StripCurrent);
        }
        else
        {
            _strip.Exit();
            OnPageChanged();
        }

        SyncSlider();
        UpdateCursor();
    }

    /// <summary>Pushes the model into the frame's transforms.</summary>
    private void Apply()
    {
        if (_strip.IsActive)
        {
            Spread.Visibility = Visibility.Collapsed;
            UpdateCursor();
            return;
        }

        var content = _transform.Content;
        if (!_transform.HasContent)
        {
            Spread.Visibility = Visibility.Collapsed;
            return;
        }

        Spread.Visibility = Visibility.Visible;

        // The frame is laid out unrotated; rotate around its centre, then move the rotated bounds to the origin.
        var rotated = _transform.Rotation % 180 != 0;
        var width = rotated ? content.Height : content.Width;
        var height = rotated ? content.Width : content.Height;
        Spread.Width = width;
        Spread.Height = height;
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
            : _transform.CanPan && !_strip.IsActive ? Cursors.Hand
            : null;

    /// <summary>Keys in strip mode; false when the key is not a strip key.</summary>
    private bool HandleStripKey(Key key)
    {
        var rtl = _vm.RightToLeft;
        switch (key)
        {
            case Key.Right:
                _strip.ScrollBy(rtl ? -0.5 : 0.5);
                return true;
            case Key.Left:
                _strip.ScrollBy(rtl ? 0.5 : -0.5);
                return true;
            case Key.PageDown or Key.Space or Key.Down:
                _strip.ScrollBy(0.9);
                return true;
            case Key.PageUp or Key.Back or Key.Up:
                _strip.ScrollBy(-0.9);
                return true;
            case Key.Home:
                _strip.Home();
                return true;
            case Key.End:
                _strip.End();
                return true;
            case Key.D0 or Key.NumPad0:
                _strip.ResetZoom();
                return true;
            case Key.OemPlus or Key.Add:
                _strip.ZoomAt(ViewTransformModel.Step, new Point(Stage.ActualWidth / 2, Stage.ActualHeight / 2));
                return true;
            case Key.OemMinus or Key.Subtract:
                _strip.ZoomAt(1 / ViewTransformModel.Step, new Point(Stage.ActualWidth / 2, Stage.ActualHeight / 2));
                return true;
            case Key.D1 or Key.NumPad1 or Key.R:
                return true; // actual size and rotation do not apply to a strip
            default:
                return false;
        }
    }

    // ---- keyboard ------------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var rtl = _vm.RightToLeft;
        var handled = true;
        if (_strip.IsActive && !ctrl && HandleStripKey(e.Key))
        {
            _vm.NoteInput();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Right when !ctrl:
                Step(forward: !rtl);
                break;
            case Key.Left when !ctrl:
                Step(forward: rtl);
                break;
            case Key.PageDown or Key.Space or Key.Down when !ctrl:
                _vm.Next();
                break;
            case Key.PageUp or Key.Back or Key.Up when !ctrl:
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
            case Key.D:
                _vm.ToggleSpread();
                break;
            case Key.M when _vm.IsBook:
                _vm.ToggleDirection();
                break;
            case Key.S when _vm.IsBook:
                _vm.ToggleCoverAlone();
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

        if (handled)
        {
            _vm.NoteInput();
        }

        e.Handled = handled;
    }

    private void Step(bool forward)
    {
        if (forward)
        {
            _vm.Next();
        }
        else
        {
            _vm.Previous();
        }
    }

    // ---- mouse ---------------------------------------------------------------------------------

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _vm.NoteInput();
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (_strip.IsActive)
        {
            if (ctrl)
            {
                _strip.ZoomAt(Math.Pow(ViewTransformModel.Step, e.Delta / 120.0), e.GetPosition(Stage));
            }
            else
            {
                _strip.ScrollBy(-e.Delta / 120.0 * 0.25); // wheel down = forward
            }
        }
        else if (ctrl)
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
        _vm.NoteInput();
        if (e.ChangedButton == MouseButton.XButton1)
        {
            _ = _vm.CloseAsync();
            e.Handled = true;
        }
    }

    /// <summary>Right-click on a page: open it in a borderless window, or show its file.</summary>
    private void OnStageRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_vm.Count == 0)
        {
            return;
        }

        // In a spread or a strip, the page under the cursor.
        var index = _strip.IsActive ? _strip.IndexAt(e.GetPosition(Stage))
            : _vm.IsSpread && PageImage2.IsVisible && PageImage2.IsMouseOver ? _vm.Frame.Last
            : _vm.Frame.First;
        var menu = new ContextMenu { PlacementTarget = Stage };
        if (_vm.ImageWindows is not null)
        {
            var open = new MenuItem { Header = Loc.Get("Viewer.Menu.OpenInWindow") };
            open.Click += (_, _) => _ = _vm.OpenInWindowAsync(index);
            menu.Items.Add(open);
        }

        if (_vm.FilePathOf(index) is not null)
        {
            var show = new MenuItem { Header = Loc.Get("Viewer.Menu.ShowInExplorer") };
            show.Click += (_, _) => _vm.ShowInExplorer(index);
            menu.Items.Add(show);
        }

        if (menu.Items.Count > 0)
        {
            menu.IsOpen = true;
        }
    }

    /// <summary>Which third of the stage a point is in: -1 left, 0 middle, 1 right.</summary>
    private int ThirdAt(Point position)
    {
        var width = Stage.ActualWidth;
        return width <= 0 ? 0 : position.X < width / 3 ? -1 : position.X > width * 2 / 3 ? 1 : 0;
    }

    private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.Focus(this);
        var position = e.GetPosition(Stage);
        _pressAt = position;
        _pressClicks = e.ClickCount;
        _dragged = false;

        if (_strip.IsActive)
        {
            // Strip: always grab-scroll; a click without a drag toggles the chrome (on release).
            _dragStart = position;
            Stage.CaptureMouse();
            UpdateCursor();
            e.Handled = true;
            return;
        }

        // Book mode: a double click in a side third is two page turns, not a zoom.
        var bookSide = _vm.IsBook && !_transform.CanPan && ThirdAt(position) != 0;
        if (e.ClickCount == 2 && !bookSide)
        {
            _transform.ToggleFitActual(position);
            Apply();
            _pressAt = null;
            e.Handled = true;
            return;
        }

        if (_transform.CanPan)
        {
            _dragStart = position;
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

        if (_pressAt is { } pressed && (position - pressed).Length > ClickSlop)
        {
            _dragged = true;
        }

        if (_dragStart is { } start && e.LeftButton == MouseButtonState.Pressed)
        {
            _dragStart = position;
            if (_strip.IsActive)
            {
                _strip.DragBy(position - start);
                return;
            }

            _transform.Pan(position - start);
            Apply();
        }
    }

    private void Stage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasPanning = _transform.CanPan;
        if (_dragStart is not null)
        {
            Stage.ReleaseMouseCapture();
            e.Handled = true;
        }

        var pressed = _pressAt;
        _pressAt = null;
        if (_strip.IsActive)
        {
            if (pressed is not null && !_dragged && _pressClicks == 1)
            {
                ToggleChrome();
            }

            e.Handled = true;
            return;
        }

        if (!_vm.IsBook || pressed is null || _dragged || wasPanning)
        {
            return; // click navigation only when not zoomed in and the press was not a drag
        }

        switch (ThirdAt(e.GetPosition(Stage)))
        {
            case -1:
                Step(forward: _vm.RightToLeft);
                break;
            case 1:
                Step(forward: !_vm.RightToLeft);
                break;
            default:
                if (_pressClicks == 1)
                {
                    ToggleChrome();
                }

                break;
        }

        e.Handled = true;
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
        _strip.Render();
    }

    // ---- slider --------------------------------------------------------------------------------

    private void SyncSlider()
    {
        if (_sliderDragging)
        {
            return;
        }

        _updatingSlider = true;
        PageSlider.Value = _vm.IsStrip ? _vm.StripCurrent : _vm.Index;
        _updatingSlider = false;
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider)
        {
            return;
        }

        var page = (int)Math.Round(e.NewValue);
        if (_sliderDragging)
        {
            SliderPopupText.Text = Loc.Format("Viewer.Book.GoToPage", page + 1, _vm.Count);
            return;
        }

        _vm.GoTo(page); // a click on the track
    }

    private void BeginSliderDrag()
    {
        _sliderDragging = true;
        SliderPopupText.Text = Loc.Format("Viewer.Book.GoToPage", (int)Math.Round(PageSlider.Value) + 1, _vm.Count);
        SliderPopup.IsOpen = true;
    }

    private void EndSliderDrag()
    {
        _sliderDragging = false;
        SliderPopup.IsOpen = false;
        _vm.GoTo((int)Math.Round(PageSlider.Value));
        SyncSlider();
    }

    // ---- chrome --------------------------------------------------------------------------------

    private void ShowChrome()
    {
        TopBar.Visibility = Visibility.Visible;
        BottomBar.Visibility = Visibility.Visible;
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
        if (TopBar.IsMouseOver || BottomBar.IsMouseOver || _dragStart is not null || _sliderDragging)
        {
            _chromeTimer.Start();
            return;
        }

        HideChromeNow();
    }

    private void HideChromeNow()
    {
        _chromeTimer.Stop();
        TopBar.Visibility = Visibility.Collapsed;
        BottomBar.Visibility = Visibility.Collapsed;
        _chromeHidden = true;
        UpdateCursor();
    }

    private void ToggleChrome()
    {
        if (TopBar.Visibility == Visibility.Visible)
        {
            HideChromeNow();
        }
        else
        {
            ShowChrome();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _ = _vm.CloseAsync();

    private void FullScreen_Click(object sender, RoutedEventArgs e) => _vm.ToggleFullScreen();

    private void Spread_Click(object sender, RoutedEventArgs e) => _vm.ToggleSpread();

    private void Direction_Click(object sender, RoutedEventArgs e) => _vm.ToggleDirection();

    private void Cover_Click(object sender, RoutedEventArgs e) => _vm.ToggleCoverAlone();

    private void Info_Click(object sender, RoutedEventArgs e) => _vm.IsInfoVisible = !_vm.IsInfoVisible;

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        if (!_strip.IsActive)
        {
            _transform.Rotate();
            Apply();
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomStep(ViewTransformModel.Step);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomStep(1 / ViewTransformModel.Step);

    /// <summary>Zoom buttons: around the centre of the view, in either flow.</summary>
    private void ZoomStep(double factor)
    {
        _vm.NoteInput();
        if (_strip.IsActive)
        {
            _strip.ZoomAt(factor, new Point(Stage.ActualWidth / 2, Stage.ActualHeight / 2));
            return;
        }

        _transform.ZoomBy(factor);
        Apply();
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        if (_strip.IsActive)
        {
            _strip.ResetZoom();
            return;
        }

        _transform.Fit();
        Apply();
    }

    private void Actual_Click(object sender, RoutedEventArgs e)
    {
        _transform.ActualSize();
        Apply();
    }
}
