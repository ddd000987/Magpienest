using System.Diagnostics;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Game;

/// <summary>
/// Starts games. Every way of playing (double-click, the "Play" command, the details button) goes through
/// <see cref="PlayAsync"/>, which also starts play-time tracking.
/// </summary>
internal sealed class GameLauncher(ILibraryService library, IShellService shell, ILog log, PlaySessionTracker tracker)
{
    private readonly object _gate = new();
    private readonly HashSet<long> _launching = [];

    /// <summary>Starts the game's program and records that it was played. Never throws.</summary>
    /// <param name="item">The game.</param>
    public async Task PlayAsync(LibraryItem item)
    {
        if (!BeginLaunch(item.Id))
        {
            shell.Notify(Loc.Format("Game.Running.Already", item.Title), NotifySeverity.Info);
            return;
        }

        try
        {
            if (!await Task.Run(() => File.Exists(item.Path)))
            {
                shell.Notify(Loc.Format("Game.Error.Missing", item.Title), NotifySeverity.Warning);
                return;
            }

            var extra = item.GetExtra<GameExtra>();
            var start = new ProcessStartInfo
            {
                FileName = item.Path,
                Arguments = extra.Arguments,
                WorkingDirectory = GameExtra.EffectiveWorkingDir(item),
                UseShellExecute = true,
            };
            var pid = await Task.Run(() =>
            {
                using var process = Process.Start(start);
                return process?.Id;
            });

            // Track before the library announces the change, so the refreshed tile already says "Running".
            tracker.Start(item, pid);
            await library.UpdateAsync(item.Id, new ItemPatch { MarkOpened = true, Status = ItemStatus.InProgress });
        }
        catch (Exception ex)
        {
            // Includes the user cancelling the administrator prompt (Win32Exception 1223).
            log.Warn($"Starting {item.Path} failed.", ex);
            shell.Notify(Loc.Format("Game.Error.Launch", item.Title), NotifySeverity.Error);
        }
        finally
        {
            lock (_gate)
            {
                _launching.Remove(item.Id);
            }
        }
    }

    /// <summary>False when the game runs already or is being started (a double click).</summary>
    private bool BeginLaunch(long id)
    {
        lock (_gate)
        {
            return !tracker.IsTracking(id) && _launching.Add(id);
        }
    }

    /// <summary>Opens a folder in Explorer. Never throws.</summary>
    /// <param name="folder">The folder.</param>
    public async Task OpenFolderAsync(string folder)
    {
        try
        {
            if (!await Task.Run(() => Directory.Exists(folder)))
            {
                shell.Notify(Loc.Format("Game.Error.FolderMissing", folder), NotifySeverity.Warning);
                return;
            }

            await Task.Run(() => Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true })?.Dispose());
        }
        catch (Exception ex)
        {
            log.Warn($"Opening {folder} failed.", ex);
            shell.Notify(Loc.Format("Game.Error.FolderMissing", folder), NotifySeverity.Error);
        }
    }
}
