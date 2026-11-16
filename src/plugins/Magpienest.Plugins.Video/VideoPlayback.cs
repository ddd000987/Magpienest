using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Media;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Video;

/// <summary>Plays videos, each in a frameless window of its own; several may be open at once. UI thread only.</summary>
internal sealed class VideoPlayback
{
    private readonly IPluginContext _ctx;
    private readonly IMediaEngine _engine;
    private readonly ILibraryService _library;
    private readonly IShellService _shell;
    private readonly VideoSettings _settings;
    private readonly List<PlayerWindow> _windows = [];

    public VideoPlayback(IPluginContext ctx, IMediaEngine engine, ILibraryService library, IShellService shell,
                         VideoSettings settings)
    {
        _ctx = ctx;
        _engine = engine;
        _library = library;
        _shell = shell;
        _settings = settings;
        ctx.Track(() =>
        {
            foreach (var window in _windows.ToList())
            {
                window.Close();
            }
        });
    }

    /// <summary>Plays the item (double-click); previous/next follow the grid's current order (missing files skipped).</summary>
    public Task OpenAsync(BrowserOpenRequest request)
    {
        var present = request.VisibleItems.Where(i => !i.IsMissing).ToList();
        var index = present.FindIndex(i => i.Id == request.Item.Id);
        if (request.Item.IsMissing || index < 0)
        {
            _shell.Notify(Loc.Format("Video.Missing", request.Item.Title), NotifySeverity.Warning);
            return Task.CompletedTask;
        }

        return OpenAsync(present, index, fromStart: false);
    }

    /// <summary>Plays one item (context-menu "Play from the beginning").</summary>
    public Task OpenAsync(LibraryItem item, bool fromStart)
    {
        if (item.IsMissing)
        {
            _shell.Notify(Loc.Format("Video.Missing", item.Title), NotifySeverity.Warning);
            return Task.CompletedTask;
        }

        return OpenAsync([item], 0, fromStart);
    }

    /// <summary>
    /// A video's size in pixels: the stored one, else probed now (and stored, so older entries without one are
    /// filled in). Null when it cannot be read.
    /// </summary>
    internal async Task<(int Width, int Height)?> SizeOfAsync(LibraryItem item)
    {
        if (VideoExtra.SizeOf(item) is { } known)
        {
            return known;
        }

        try
        {
            var info = await _engine.ProbeAsync(item.Path, _ctx.Deactivating);
            if (info is not { VideoWidth: > 0, VideoHeight: > 0 })
            {
                return null;
            }

            _ = _library.UpdateAsync(item.Id, new ItemPatch
            {
                ProgressTotal = info.Duration.Ticks > 0 ? (long)info.Duration.TotalMilliseconds : null,
                Extra = LibraryItemExtensions.SerializeExtra(new VideoExtra { Width = info.VideoWidth, Height = info.VideoHeight }),
            });
            return (info.VideoWidth, info.VideoHeight);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ctx.Log.Debug($"Reading the size of {item.Path} failed: {ex.Message}");
            return null;
        }
    }

    private async Task OpenAsync(IReadOnlyList<LibraryItem> playlist, int index, bool fromStart)
    {
        // A video that is already playing is brought forward instead of opened twice.
        if (FindWindow(playlist[index]) is { } open && !fromStart)
        {
            open.Activate();
            return;
        }

        // The window takes the video's shape from the start.
        var size = await SizeOfAsync(playlist[index]);
        if (FindWindow(playlist[index]) is { } existing)
        {
            if (!fromStart)
            {
                existing.Activate();
                return;
            }

            existing.Close();
        }

        var session = new PlayerSession(_engine.CreatePlayer(), playlist, index, _library, _settings, _shell, _ctx.Log);
        PlayerWindow window;
        try
        {
            window = new PlayerWindow(session, size, SizeOfAsync, _settings, _shell);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
        window.Show();
        window.Activate();
        session.Start(fromStart);
    }

    private PlayerWindow? FindWindow(LibraryItem item) => _windows.FirstOrDefault(w => w.Session.Current.Id == item.Id);
}
