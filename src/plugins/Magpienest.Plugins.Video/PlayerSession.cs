using System.Diagnostics;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Media;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Video;

/// <summary>
/// One player overlay: the playlist (the grid's visible videos), resume, saving progress every few seconds and on
/// switch/close, watch time (only while playing) and what happens at the end. UI thread only.
/// </summary>
internal sealed class PlayerSession : IDisposable
{
    /// <summary>How often the position is saved while playing.</summary>
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<LibraryItem> _playlist;
    private readonly ILibraryService _library;
    private readonly VideoSettings _settings;
    private readonly IShellService _shell;
    private readonly ILog _log;
    private readonly Stopwatch _watching = new();
    private WatchSession? _watch;
    private TimeSpan? _startAt;
    private bool _ended;
    private bool _disposed;

    public PlayerSession(IMediaPlayer player, IReadOnlyList<LibraryItem> playlist, int index, ILibraryService library,
                         VideoSettings settings, IShellService shell, ILog log)
    {
        Player = player;
        _playlist = playlist;
        Index = Math.Clamp(index, 0, playlist.Count - 1);
        _library = library;
        _settings = settings;
        _shell = shell;
        _log = log;

        player.Volume = settings.Volume;
        player.IsMuted = settings.Muted;
        player.Rate = settings.Rate;
        player.StateChanged += OnStateChanged;
        player.EndReached += OnEndReached;
        player.Failed += OnFailed;
    }

    /// <summary>A different video started.</summary>
    public event EventHandler? CurrentChanged;

    /// <summary>The video reached its end and nothing follows automatically.</summary>
    public event EventHandler? Ended;

    public IMediaPlayer Player { get; }

    public int Index { get; private set; }

    public LibraryItem Current => _playlist[Index];

    public int Count => _playlist.Count;

    public bool HasPrevious => Index > 0;

    public bool HasNext => Index < _playlist.Count - 1;

    /// <summary>Saves (when playing) — called by the view's timer every <see cref="SaveInterval"/>.</summary>
    public void Tick()
    {
        if (Player.IsPlaying)
        {
            Save(ended: false);
        }
    }

    public void Start(bool fromStart) => Open(Index, fromStart);

    public void Previous()
    {
        if (HasPrevious)
        {
            Switch(Index - 1);
        }
    }

    public void Next()
    {
        if (HasNext)
        {
            Switch(Index + 1);
        }
    }

    /// <summary>Saves the last state and releases the player.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_watch is not null && !_ended)
        {
            Save(ended: false);
        }

        _disposed = true;
        Player.StateChanged -= OnStateChanged;
        Player.EndReached -= OnEndReached;
        Player.Failed -= OnFailed;
        Player.Dispose();
    }

    private void Switch(int index)
    {
        if (_watch is not null && !_ended)
        {
            Save(ended: false);
        }

        Open(index, fromStart: false);
    }

    private void Open(int index, bool fromStart)
    {
        Index = index;
        var item = Current;
        var remember = _settings.RememberPosition;

        // Without "remember where I stopped" there is no watch session: nothing is resumed or saved.
        _watch = remember ? new WatchSession(item.Status) : null;
        _startAt = fromStart || !remember ? null : WatchSession.StartAt(item);
        _ended = false;
        _watching.Reset();
        Player.Open(item.Path, _startAt);
        if (remember)
        {
            Save(ended: false);
        }
        else
        {
            _ = SaveAsync(item.Id, new ItemPatch { MarkOpened = true });   // "last opened" sorting still works
        }

        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Save(bool ended)
    {
        if (_watch is null)
        {
            return;
        }

        var watched = _watching.Elapsed;
        if (_watching.IsRunning)
        {
            _watching.Restart();
        }
        else
        {
            _watching.Reset();
        }

        // Right after opening the player has no position yet: keep the one we started from.
        var position = Player.Position > TimeSpan.Zero ? Player.Position : _startAt ?? TimeSpan.Zero;
        var patch = _watch.ToPatch(position, Player.Duration, watched, ended);
        var id = Current.Id;
        _ = SaveAsync(id, patch);
    }

    private async Task SaveAsync(long id, ItemPatch patch)
    {
        try
        {
            await _library.UpdateAsync(id, patch);
        }
        catch (Exception ex)
        {
            _log.Warn("Saving video progress failed.", ex);
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (Player.IsPlaying)
        {
            _ended = false;   // replaying after the end
            _watching.Start();
        }
        else
        {
            _watching.Stop();
        }
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        _watching.Stop();
        Save(ended: true);
        _ended = true;
        if (_settings.ContinueWithNext && HasNext)
        {
            Next();
            return;
        }

        Ended?.Invoke(this, EventArgs.Empty);
    }

    private void OnFailed(object? sender, string message)
    {
        _log.Warn($"Playing {Current.Path} failed: {message}");
        _shell.Notify(Loc.Format("Video.Error.Play", Current.Title), NotifySeverity.Error);
    }
}
