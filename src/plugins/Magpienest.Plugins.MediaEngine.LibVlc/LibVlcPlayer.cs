using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using Magpienest.Sdk;
using Magpienest.Sdk.Media;
using MediaTrack = Magpienest.Sdk.Media.MediaTrack;
using TrackDescription = LibVLCSharp.Shared.Structures.TrackDescription;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Magpienest.Plugins.MediaEngine.LibVlc;

/// <summary>
/// <see cref="IMediaPlayer"/> on one LibVLC media player at a time. LibVLC events arrive on LibVLC threads and are
/// forwarded to the UI thread; nothing calls back into LibVLC from an event handler. Stopping can block for
/// seconds, so a player that is stopped (or replaced by opening another file) is detached at once and stopped and
/// disposed on the thread pool, and a fresh one takes its place.
/// </summary>
internal sealed class LibVlcPlayer : IMediaPlayer
{
    /// <summary>Minimum time between two <see cref="PositionChanged"/> events.</summary>
    public const long PositionIntervalMs = 250;

    private readonly LibVlcEngine _engine;
    private readonly ILog _log;
    private readonly Dispatcher _dispatcher;
    private VlcPlayer _player;
    private Media? _media;
    private string? _path;
    private VideoView? _view;
    private bool _viewLoaded;
    private bool _disposed;
    private Hooks? _hooks;
    private int _generation;   // UI thread only
    private Media? _pendingOpen;
    private Task _released = Task.CompletedTask;   // the previous player stopping and being released
    private bool _playing;     // from Playing/Paused/Stopped/EndReached events of the current player
    private BlackBackground? _background;
    private IntPtr _backgroundHwnd;
    private long _lastPosition;
    private int _volume = 100;
    private bool _muted;
    private double _rate = 1.0;

    public LibVlcPlayer(LibVlcEngine engine, ILog log)
    {
        _engine = engine;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _player = NewPlayer();
    }

    /// <summary>Extra LibVLC media options (tests use ":no-video" so no window opens).</summary>
    internal IReadOnlyList<string> MediaOptions { get; init; } = [];

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public event EventHandler? PositionChanged;

    /// <inheritdoc />
    public event EventHandler? EndReached;

    /// <inheritdoc />
    public event EventHandler<string>? Failed;

    /// <inheritdoc />
    public bool IsPlaying => _media is not null && _playing;

    /// <inheritdoc />
    public bool HasMedia => _media is not null;

    /// <inheritdoc />
    public TimeSpan Duration => _media is not null && _player.Length > 0
        ? TimeSpan.FromMilliseconds(_player.Length)
        : TimeSpan.Zero;

    /// <inheritdoc />
    public TimeSpan Position
    {
        get => _media is not null && _player.Time > 0 ? TimeSpan.FromMilliseconds(_player.Time) : TimeSpan.Zero;
        set
        {
            if (_media is null || _path is null)
            {
                return;
            }

            var target = value < TimeSpan.Zero ? TimeSpan.Zero : value;
            if (_player.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
            {
                // A finished player cannot seek; start the file again at the requested place.
                Open(_path, target);
                return;
            }

            _player.Time = (long)target.TotalMilliseconds;
        }
    }

    /// <inheritdoc />
    public int Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 200); // LibVLC amplifies above 100.
            _player.Volume = _volume;
            RaiseState();
        }
    }

    /// <inheritdoc />
    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            _player.Mute = value;
            RaiseState();
        }
    }

    /// <inheritdoc />
    public double Rate
    {
        get => _rate;
        set
        {
            _rate = Math.Clamp(value, 0.25, 4.0);
            _player.SetRate((float)_rate);
            RaiseState();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> AudioTracks => Tracks(_media is null ? [] : _player.AudioTrackDescription);

    /// <inheritdoc />
    public IReadOnlyList<MediaTrack> SubtitleTracks => Tracks(_media is null ? [] : _player.SpuDescription);

    /// <inheritdoc />
    public int? CurrentAudioTrack
    {
        get => _media is not null && _player.AudioTrack >= 0 ? _player.AudioTrack : null;
        set
        {
            if (_media is not null)
            {
                _player.SetAudioTrack(value ?? -1);
                RaiseState();
            }
        }
    }

    /// <inheritdoc />
    public int? CurrentSubtitleTrack
    {
        get => _media is not null && _player.Spu >= 0 ? _player.Spu : null;
        set
        {
            if (_media is not null)
            {
                _player.SetSpu(value ?? -1);
                RaiseState();
            }
        }
    }

    /// <inheritdoc />
    public FrameworkElement CreateView(FrameworkElement? overlay)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_view is not null)
        {
            throw new InvalidOperationException("CreateView may be called only once.");
        }

        _view = new VideoView { Content = overlay };

        // Assigning the player before the view is loaded fails; it needs the native window handle.
        _view.Loaded += (_, _) =>
        {
            _viewLoaded = true;
            if (_disposed)
            {
                return;
            }

            if (_view.MediaPlayer != _player)
            {
                _view.MediaPlayer = _player;
            }

            var hwnd = _player.Hwnd;
            if (_background is null || _backgroundHwnd != hwnd)
            {
                _background?.Dispose();
                _background = BlackBackground.Attach(hwnd);
                _backgroundHwnd = hwnd;
            }

            // A file opened before the video surface existed starts now, so LibVLC draws into the view
            // instead of opening a window of its own.
            if (_pendingOpen is { } pending)
            {
                _pendingOpen = null;
                Start(pending);
            }
        };
        _view.Unloaded += (_, _) => _viewLoaded = false;
        return _view;
    }

    /// <inheritdoc />
    public void Open(string path, TimeSpan? startAt = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_media is not null)
        {
            ReplacePlayer();
        }

        var media = new Media(_engine.Vlc, path, FromType.FromPath);
        foreach (var option in MediaOptions)
        {
            media.AddOption(option);
        }

        if (startAt is { TotalSeconds: > 0 } start)
        {
            media.AddOption(string.Create(CultureInfo.InvariantCulture, $":start-time={start.TotalSeconds:0.###}"));
        }

        _media = media;
        _path = path;
        _lastPosition = 0;
        if (_view is not null && !_viewLoaded)
        {
            _pendingOpen = media;
        }
        else
        {
            Start(media);
        }

        RaiseState();
    }

    private void Start(Media media)
    {
        // LibVLC lets one video output use a window at a time; while the previous player still holds the
        // view's window, a new output would open a window of its own. Start once it has stopped.
        if (!_released.IsCompleted)
        {
            var generation = _generation;
            _released.ContinueWith(_ => _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed && generation == _generation && ReferenceEquals(_media, media))
                {
                    Start(media);
                }
            }), TaskScheduler.Default);
            return;
        }

        if (!_player.Play(media))
        {
            Failed?.Invoke(this, $"LibVLC could not start {_path}.");
        }
    }

    /// <inheritdoc />
    public void Play()
    {
        if (_media is null || _path is null)
        {
            return;
        }

        if (_player.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
        {
            Open(_path);
            return;
        }

        _player.SetPause(false);
    }

    /// <inheritdoc />
    public void Pause()
    {
        if (_media is not null && _player.CanPause)
        {
            _player.SetPause(true);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_media is null)
        {
            return;
        }

        ReplacePlayer();
        RaiseState();
    }

    /// <inheritdoc />
    public bool AddSubtitleFile(string path)
    {
        if (_media is null)
        {
            return false;
        }

        try
        {
            return _player.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _background?.Dispose();
        _background = null;
        var view = _view;
        var release = Detach();
        _ = _engine.Release(release).ContinueWith(
            _ => _dispatcher.BeginInvoke(() => view?.Dispose()), TaskScheduler.Default);
        StateChanged = null;
        PositionChanged = null;
        EndReached = null;
        Failed = null;
    }

    private static MediaTrack[] Tracks(TrackDescription[] tracks) =>
        [.. tracks.Where(t => t.Id != -1).Select(t => new MediaTrack(t.Id, t.Name ?? ""))];

    private VlcPlayer NewPlayer()
    {
        var player = new VlcPlayer(_engine.Vlc);
        _hooks = new Hooks(this, player, ++_generation);
        _hooks.Attach();
        return player;
    }

    /// <summary>Unhooks the current player and media and returns the blocking work that releases them.</summary>
    private Action Detach()
    {
        var player = _player;
        var media = _media;
        _hooks?.Detach();
        _hooks = null;
        _pendingOpen = null;
        _playing = false;
        _media = null;
        return () =>
        {
            player.Stop();
            player.Dispose();
            media?.Dispose();
        };
    }

    private void ReplacePlayer()
    {
        var release = Detach();
        _player = NewPlayer();
        if (_view is not null && _viewLoaded)
        {
            _view.MediaPlayer = _player;
        }

        _released = _engine.Release(release);
    }

    // ---- UI thread, forwarded from LibVLC threads ------------------------------------------------

    private void HandleStopped()
    {
        _playing = false;
        RaiseState();
    }

    private void HandlePlaying()
    {
        _playing = true;
        // Volume, mute and rate set before the audio output existed are lost; apply them again.
        _player.Volume = _volume;
        _player.Mute = _muted;
        if (Math.Abs(_rate - 1.0) > 0.001)
        {
            _player.SetRate((float)_rate);
        }

        RaiseState();
    }

    private void HandleEnd()
    {
        _playing = false;
        RaiseState();
        EndReached?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>LibVLC thread: at most one position event per <see cref="PositionIntervalMs"/>.</summary>
    private void OnTime(int generation)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastPosition) < PositionIntervalMs)
        {
            return;
        }

        Interlocked.Exchange(ref _lastPosition, now);
        Forward(generation, () => PositionChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Runs on the UI thread, only if the event's player is still the current one. (The sender of a LibVLCSharp
    /// event is its internal event manager, not the player, so players are told apart by a generation number.)
    /// </summary>
    private void Forward(int generation, Action action) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || generation != _generation)
            {
                return;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                _log.Warn("A media player event handler failed.", ex);
            }
        });

    private void RaiseState()
    {
        // The picture may have changed size: refill the area beside it.
        _background?.Repaint();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The event subscriptions of one LibVLC player, tagged with its generation.</summary>
    private sealed class Hooks(LibVlcPlayer owner, VlcPlayer player, int generation)
    {
        public void Attach()
        {
            player.Playing += OnPlaying;
            player.Paused += OnStopped;
            player.Stopped += OnStopped;
            player.LengthChanged += OnState;
            player.ESAdded += OnState;
            player.ESDeleted += OnState;
            player.ESSelected += OnState;
            player.Vout += OnState;
            player.TimeChanged += OnTime;
            player.EndReached += OnEnd;
            player.EncounteredError += OnError;
        }

        public void Detach()
        {
            player.Playing -= OnPlaying;
            player.Paused -= OnStopped;
            player.Stopped -= OnStopped;
            player.LengthChanged -= OnState;
            player.ESAdded -= OnState;
            player.ESDeleted -= OnState;
            player.ESSelected -= OnState;
            player.Vout -= OnState;
            player.TimeChanged -= OnTime;
            player.EndReached -= OnEnd;
            player.EncounteredError -= OnError;
        }

        private void OnPlaying(object? sender, EventArgs e) => owner.Forward(generation, owner.HandlePlaying);

        private void OnState(object? sender, EventArgs e) => owner.Forward(generation, owner.RaiseState);

        private void OnStopped(object? sender, EventArgs e) => owner.Forward(generation, owner.HandleStopped);

        private void OnTime(object? sender, MediaPlayerTimeChangedEventArgs e) => owner.OnTime(generation);

        private void OnEnd(object? sender, EventArgs e) => owner.Forward(generation, owner.HandleEnd);

        private void OnError(object? sender, EventArgs e)
        {
            var path = owner._path;
            owner.Forward(generation, () =>
            {
                owner._playing = false;
                owner.RaiseState();
                owner.Failed?.Invoke(owner, $"LibVLC reported an error playing {path}.");
            });
        }
    }
}
