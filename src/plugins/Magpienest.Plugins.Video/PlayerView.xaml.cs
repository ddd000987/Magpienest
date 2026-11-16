using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Media;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Video;

/// <summary>The window a player is shown in.</summary>
internal interface IPlayerHost
{
    /// <summary>Whether the host fills the screen.</summary>
    bool IsFullScreen { get; set; }

    /// <summary>Moves the window with the mouse until the left button is released.</summary>
    void BeginMove();
}

/// <summary>
/// The player's controls, drawn above the video (it is the overlay passed to <see cref="IMediaPlayer.CreateView"/>).
/// Only input and display live here; resume, saving and the playlist are in <see cref="PlayerSession"/>.
/// </summary>
internal sealed partial class PlayerView : UserControl
{
    /// <summary>Bars and cursor hide after this long without input while playing.</summary>
    public static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(3);

    /// <summary>Seeks while the thumb is dragged happen at most this often.</summary>
    public static readonly TimeSpan DragSeekInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Speeds offered in the menu and stepped through with [ and ].</summary>
    internal static readonly double[] Speeds = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];

    private const int VolumeStep = 5;

    /// <summary>How long the volume badge stays after the last change.</summary>
    private static readonly TimeSpan VolumeBadgeTime = TimeSpan.FromSeconds(1.2);

    // Alpha 1: invisible, but keeps the layered overlay window hit-testable (alpha 0 lets clicks through).
    private static readonly Brush HitTestBrush = CreateHitTestBrush();

    private readonly PlayerSession _session;
    private readonly VideoSettings _settings;
    private readonly IShellService _shell;
    private readonly IPlayerHost _host;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _volumeBadgeTimer;
    private bool _updating;
    private bool _dragging;
    private DateTime _lastDragSeek;
    private bool _menuOpen;
    private bool _closed;
    private bool _pressed;          // left button down on the picture, not yet a click or a drag
    private Point _pressPoint;

    public PlayerView(PlayerSession session, VideoSettings settings, IShellService shell, IPlayerHost host)
    {
        _session = session;
        _settings = settings;
        _shell = shell;
        _host = host;
        InitializeComponent();
        Root.Background = HitTestBrush;

        _saveTimer = new DispatcherTimer { Interval = PlayerSession.SaveInterval };
        _saveTimer.Tick += (_, _) => _session.Tick();
        _hideTimer = new DispatcherTimer { Interval = HideDelay };
        _hideTimer.Tick += (_, _) => TryHideBars();
        _volumeBadgeTimer = new DispatcherTimer { Interval = VolumeBadgeTime };
        _volumeBadgeTimer.Tick += (_, _) =>
        {
            _volumeBadgeTimer.Stop();
            VolumeBadgeText.Visibility = Visibility.Collapsed;
        };

        Player.StateChanged += (_, _) => UpdateState();
        Player.PositionChanged += (_, _) => UpdatePosition();
        session.CurrentChanged += (_, _) => UpdateItem();
        session.Ended += (_, _) => ShowBars();

        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _dragging = false;
            SeekToSlider();
        }));
        SeekSlider.ValueChanged += SeekSlider_ValueChanged;
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;

        PreviewKeyDown += OnPreviewKeyDown;
        Root.MouseMove += Root_MouseMove;
        Root.MouseLeave += (_, _) =>
        {
            if (!_menuOpen && !_dragging && !_pressed)
            {
                HideBars();
            }
        };
        Root.MouseLeftButtonDown += Root_MouseLeftButtonDown;
        Root.MouseLeftButtonUp += Root_MouseLeftButtonUp;
        Root.MouseWheel += Root_MouseWheel;
        SizeChanged += (_, e) => FitControls(e.NewSize.Width);
        Loaded += (_, _) =>
        {
            Focus();
            _saveTimer.Start();
            ShowBars();
        };

        UpdateItem();
        UpdateState();
        UpdateVolume();
    }

    /// <summary>The user asked to close the player.</summary>
    public event EventHandler? CloseRequested;

    private IMediaPlayer Player => _session.Player;

    /// <summary>Stops the timers.</summary>
    public void Shutdown()
    {
        _closed = true;
        _saveTimer.Stop();
        _hideTimer.Stop();
        _volumeBadgeTimer.Stop();
    }

    /// <summary>The host's fullscreen state changed from outside the controls.</summary>
    public void OnHostChanged() => UpdateState();

    // ---- display -------------------------------------------------------------------------------

    private void UpdateItem()
    {
        TitleText.Text = _session.Current.Title;
        var several = _session.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.Visibility = several;
        NextButton.Visibility = several;
        PreviousButton.IsEnabled = _session.HasPrevious;
        NextButton.IsEnabled = _session.HasNext;
        UpdatePosition();
    }

    private void UpdateState()
    {
        var playing = Player.IsPlaying;
        PlayGlyph.Text = playing ? "" : "";
        PlayButton.ToolTip = Loc.Get(playing ? "Video.Player.Pause" : "Video.Player.Play");
        SpeedText.Text = Loc.Format("Video.Player.SpeedValue", Player.Rate.ToString("0.##", CultureInfo.CurrentCulture));
        FitControls(ActualWidth);
        FullScreenGlyph.Text = _host.IsFullScreen ? "" : "";
        FullScreenButton.ToolTip = Loc.Get(_host.IsFullScreen ? "Video.Player.ExitFullScreen" : "Video.Player.FullScreen");
        UpdateVolume();
        UpdatePosition();
        if (playing)
        {
            RestartHideTimer();
        }
        else if (Root.IsMouseOver)
        {
            ShowBars();
        }
    }

    /// <summary>Drops the least needed controls when the window is narrow (e.g. a portrait video).</summary>
    private void FitControls(double width)
    {
        VolumeSlider.Visibility = width < 620 ? Visibility.Collapsed : Visibility.Visible;
        var extras = width < 500 ? Visibility.Collapsed : Visibility.Visible;
        SpeedButton.Visibility = extras;
        // Only offered when there is a choice; most videos have one audio track.
        AudioButton.Visibility = Player.AudioTracks.Count > 1 ? extras : Visibility.Collapsed;
        SubtitleButton.Visibility = extras;
    }

    private void UpdatePosition()
    {
        var duration = Player.Duration;
        var position = Player.Position;
        DurationText.Text = VideoFormat.Duration(duration);
        if (_dragging)
        {
            return;
        }

        PositionText.Text = VideoFormat.Duration(position);
        _updating = true;
        try
        {
            SeekSlider.Maximum = Math.Max(1, duration.TotalSeconds);
            SeekSlider.Value = Math.Clamp(position.TotalSeconds, 0, SeekSlider.Maximum);
            SeekSlider.IsEnabled = duration > TimeSpan.Zero;
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateVolume()
    {
        _updating = true;
        try
        {
            VolumeSlider.Maximum = Math.Max(_settings.MaxVolume, Player.Volume); // The limit may change in settings.
            VolumeSlider.Value = Player.Volume;
        }
        finally
        {
            _updating = false;
        }

        var muted = Player.IsMuted || Player.Volume == 0;
        MuteGlyph.Text = muted ? "" : "";
        MuteButton.ToolTip = Loc.Get(Player.IsMuted ? "Video.Player.Unmute" : "Video.Player.Mute");
    }

    // ---- auto-hide -----------------------------------------------------------------------------

    private void ShowBars()
    {
        CloseButton.Visibility = Visibility.Visible;
        BottomBar.Visibility = Visibility.Visible;
        Root.ClearValue(CursorProperty);
        RestartHideTimer();
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        if (!_closed)
        {
            _hideTimer.Start();
        }
    }

    /// <summary>
    /// Hides the controls when the mouse is outside the window, or rests on the picture while playing.
    /// They stay while a menu is open or the seek bar is dragged.
    /// </summary>
    private void TryHideBars()
    {
        _hideTimer.Stop();
        if (_menuOpen)
        {
            return;   // closing the menu restarts the timer
        }

        if (_dragging || _pressed ||
            (Root.IsMouseOver && (!Player.IsPlaying || CloseButton.IsMouseOver || BottomBar.IsMouseOver)))
        {
            if (Player.IsPlaying || _dragging || _pressed)
            {
                RestartHideTimer();
            }

            return;
        }

        HideBars();
    }

    private void HideBars()
    {
        _hideTimer.Stop();
        CloseButton.Visibility = Visibility.Hidden;
        BottomBar.Visibility = Visibility.Hidden;
        Root.Cursor = Cursors.None;
    }

    // ---- actions -------------------------------------------------------------------------------

    private void TogglePlay()
    {
        if (Player.IsPlaying)
        {
            Player.Pause();
        }
        else
        {
            Player.Play();
        }
    }

    private void SeekBy(TimeSpan delta)
    {
        var target = Player.Position + delta;
        if (Player.Duration > TimeSpan.Zero && target > Player.Duration)
        {
            target = Player.Duration - TimeSpan.FromSeconds(1);
        }

        Player.Position = target < TimeSpan.Zero ? TimeSpan.Zero : target;
        UpdatePosition();
    }

    private void SeekToSlider()
    {
        if (Player.Duration > TimeSpan.Zero)
        {
            Player.Position = TimeSpan.FromSeconds(SeekSlider.Value);
            _lastDragSeek = DateTime.UtcNow;
        }
    }

    private void ChangeVolume(int delta)
    {
        Player.Volume = Math.Clamp(Player.Volume + delta, 0, _settings.MaxVolume);
        if (Player.IsMuted && delta > 0)
        {
            Player.IsMuted = false;
            _settings.Muted = false;
        }

        _settings.Volume = Player.Volume;
        UpdateVolume();
        ShowVolumeBadge();
    }

    /// <summary>Shows the volume as a number next to the slider for a moment (and the control bar with it).</summary>
    private void ShowVolumeBadge()
    {
        VolumeBadgeText.Text = Player.IsMuted
            ? Loc.Get("Video.Player.Muted")
            : Loc.Format("Video.Player.VolumeValue", Player.Volume);
        VolumeBadgeText.Visibility = Visibility.Visible;
        ShowBars();
        _volumeBadgeTimer.Stop();
        _volumeBadgeTimer.Start();
    }

    private void ToggleMute()
    {
        Player.IsMuted = !Player.IsMuted;
        _settings.Muted = Player.IsMuted;
        UpdateVolume();
        ShowVolumeBadge();
    }

    private void SetRate(double rate)
    {
        Player.Rate = rate;
        _settings.Rate = rate;
        UpdateState();
    }

    private void StepRate(int direction)
    {
        var current = Player.Rate;
        var next = direction > 0
            ? Speeds.FirstOrDefault(s => s > current + 0.001, Speeds[^1])
            : Speeds.LastOrDefault(s => s < current - 0.001, Speeds[0]);
        SetRate(next);
    }

    private void ToggleFullScreen()
    {
        _host.IsFullScreen = !_host.IsFullScreen;
        UpdateState();
        ShowBars();
    }

    private void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ---- input ---------------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var step = TimeSpan.FromSeconds(shift ? 30 : 5);
        var handled = true;
        switch (e.Key)
        {
            case Key.Space:
                TogglePlay();
                break;
            case Key.Left:
                SeekBy(-step);
                break;
            case Key.Right:
                SeekBy(step);
                break;
            case Key.Up:
                ChangeVolume(VolumeStep);
                break;
            case Key.Down:
                ChangeVolume(-VolumeStep);
                break;
            case Key.M:
                ToggleMute();
                break;
            case Key.F:
            case Key.F11:
                ToggleFullScreen();
                break;
            case Key.OemOpenBrackets:
                StepRate(-1);
                break;
            case Key.OemCloseBrackets:
                StepRate(1);
                break;
            case Key.PageUp:
                _session.Previous();
                break;
            case Key.PageDown:
                _session.Next();
                break;
            case Key.Escape:
                if (_host.IsFullScreen)
                {
                    ToggleFullScreen();
                }
                else
                {
                    RequestClose();
                }

                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            e.Handled = true;
            ShowBars();
        }
    }

    // On the picture: a click plays/pauses, a drag moves the window, a double-click toggles fullscreen
    // (and undoes the pause of its first click).

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Root))
        {
            return;
        }

        Focus();
        if (e.ClickCount == 2)
        {
            EndPress();
            TogglePlay();
            ToggleFullScreen();
        }
        else
        {
            _pressed = true;
            _pressPoint = e.GetPosition(Root);
            Root.CaptureMouse();
        }

        e.Handled = true;
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        ShowBars();
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition(Root) - _pressPoint;
        if (Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance)
        {
            EndPress();
            _host.BeginMove();
        }
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        EndPress();
        TogglePlay();
        e.Handled = true;
    }

    private void EndPress()
    {
        _pressed = false;
        if (Root.IsMouseCaptured)
        {
            Root.ReleaseMouseCapture();
        }
    }

    private void Root_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ChangeVolume(e.Delta > 0 ? VolumeStep : -VolumeStep);
        ShowBars();
        e.Handled = true;
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating)
        {
            return;
        }

        if (!_dragging || DateTime.UtcNow - _lastDragSeek >= DragSeekInterval)
        {
            SeekToSlider();
        }

        PositionText.Text = VideoFormat.Duration(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updating)
        {
            ChangeVolume((int)Math.Round(e.NewValue) - Player.Volume);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void Previous_Click(object sender, RoutedEventArgs e) => _session.Previous();

    private void Next_Click(object sender, RoutedEventArgs e) => _session.Next();

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void Speed_Click(object sender, RoutedEventArgs e) =>
        ShowMenu(SpeedButton, [.. Speeds.Select(speed => Choice(
            Loc.Format("Video.Player.SpeedValue", speed.ToString("0.##", CultureInfo.CurrentCulture)),
            Math.Abs(speed - Player.Rate) < 0.001, () => SetRate(speed)))]);

    private void Audio_Click(object sender, RoutedEventArgs e) =>
        ShowMenu(AudioButton, [.. Player.AudioTracks.Select(track => Choice(TrackName(track),
            track.Id == Player.CurrentAudioTrack, () => Player.CurrentAudioTrack = track.Id))]);

    private void Subtitle_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<Control>
        {
            Choice(Loc.Get("Video.Player.SubtitlesOff"), Player.CurrentSubtitleTrack is null,
                () => Player.CurrentSubtitleTrack = null),
        };
        items.AddRange(Player.SubtitleTracks.Select(track => Choice(TrackName(track),
            track.Id == Player.CurrentSubtitleTrack, () => Player.CurrentSubtitleTrack = track.Id)));
        items.Add(new Separator());
        var load = new MenuItem { Header = Loc.Get("Video.Player.LoadSubtitle") };
        load.Click += (_, _) => LoadSubtitle();
        items.Add(load);
        ShowMenu(SubtitleButton, items);
    }

    private void LoadSubtitle()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("Video.Player.LoadSubtitle"),
            Filter = Loc.Get("Video.Player.SubtitleFilter") + "|*.srt;*.ass;*.ssa;*.sub;*.vtt;*.idx;*.smi",
            CheckFileExists = true,
            InitialDirectory = Path.GetDirectoryName(_session.Current.Path),
        };
        if (dialog.ShowDialog(_shell.MainWindow) == true && !Player.AddSubtitleFile(dialog.FileName))
        {
            _shell.Notify(Loc.Get("Video.Error.Subtitle"), NotifySeverity.Error);
        }
    }

    private static string TrackName(MediaTrack track) =>
        string.IsNullOrWhiteSpace(track.Name) ? Loc.Format("Video.Player.Track", track.Id) : track.Name;

    private static MenuItem Choice(string header, bool isChecked, Action select)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => select();
        return item;
    }

    private void ShowMenu(FrameworkElement target, IReadOnlyList<Control> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Top };
        foreach (var item in items)
        {
            menu.Items.Add(item);
        }

        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            RestartHideTimer();
        };
        menu.IsOpen = true;
    }

    private static SolidColorBrush CreateHitTestBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        brush.Freeze();
        return brush;
    }
}
