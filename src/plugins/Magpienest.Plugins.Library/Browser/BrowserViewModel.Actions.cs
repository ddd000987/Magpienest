using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>Item actions, scanning and folder management.</summary>
internal sealed partial class BrowserViewModel
{
    private IReadOnlyList<LibraryItem> SelectedItems => [.. SelectedTiles.Select(t => t.Item)];

    /// <summary>Double-click / Enter on a tile.</summary>
    public async Task OpenAsync(TileViewModel tile)
    {
        var items = Items;
        var index = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], tile))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        try
        {
            await Options.OpenItem(new BrowserOpenRequest(tile.Item, [.. items.Select(t => t.Item)], index));
        }
        catch (Exception ex)
        {
            _services.Log.Error($"Opening {tile.Item.Path} failed.", ex);
            _services.Shell.Notify(Loc.Get("Library.Error.OpenFailed"), NotifySeverity.Error);
        }
    }

    /// <summary>Custom commands that apply to the current selection.</summary>
    public IReadOnlyList<BrowserCommand> ApplicableCommands()
    {
        var selection = SelectedItems;
        if (selection.Count == 0)
        {
            return [];
        }

        return [.. Options.Commands.Where(c =>
        {
            try
            {
                return c.CanExecute?.Invoke(selection) ?? true;
            }
            catch (Exception)
            {
                return false;
            }
        })];
    }

    public bool SelectionIsAllFavorite => SelectedTiles.Count > 0 && SelectedTiles.All(t => t.IsFavorite);

    [RelayCommand]
    private void ToggleDetails() => IsDetailsVisible = !IsDetailsVisible;

    [RelayCommand]
    private void ToggleFolderTree() => IsFolderTreeVisible = !IsFolderTreeVisible;

    [RelayCommand]
    private async Task AddItem()
    {
        if (Options.AddItem is null)
        {
            return;
        }

        try
        {
            await Options.AddItem();
        }
        catch (Exception ex)
        {
            _services.Log.Error("AddItem failed.", ex);
            _services.Shell.Notify(Loc.Get("Library.Error.SaveFailed"), NotifySeverity.Error);
        }
    }

    [RelayCommand]
    private void ShowInExplorer()
    {
        if (SelectedTiles.FirstOrDefault() is { } tile)
        {
            ExplorerLauncher.Show(tile.Item.Path, _services.Log);
        }
    }

    [RelayCommand]
    private Task SetFavorite(bool value) => PatchSelectionAsync(_ => new ItemPatch { IsFavorite = value });

    [RelayCommand]
    private Task SetRating(int value) => PatchSelectionAsync(_ => new ItemPatch { Rating = Math.Clamp(value, 0, 5) });

    [RelayCommand]
    private Task SetStatus(ItemStatus value) => PatchSelectionAsync(_ => new ItemPatch { Status = value });

    [RelayCommand]
    private async Task RunCommand(BrowserCommand? command)
    {
        var selection = SelectedItems;
        if (command is null || selection.Count == 0)
        {
            return;
        }

        try
        {
            await command.Execute(selection);
        }
        catch (Exception ex)
        {
            _services.Log.Error($"Command {command.TitleKey} failed.", ex);
            _services.Shell.Notify(Loc.Get("Library.Error.CommandFailed"), NotifySeverity.Error);
        }
    }

    /// <summary>Adds one tag to every selected item (details panel, multiple selection).</summary>
    [RelayCommand]
    private async Task AddTagToSelection(string? tag)
    {
        tag = tag?.Trim();
        if (string.IsNullOrEmpty(tag))
        {
            return;
        }

        foreach (var item in SelectedItems)
        {
            if (item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!await TryAsync(() => _services.Library.SetTagsAsync(item.Id, [.. item.Tags, tag])))
            {
                return;
            }
        }
    }

    [RelayCommand]
    private async Task EditTags()
    {
        var selection = SelectedItems;
        if (selection.Count == 0)
        {
            return;
        }

        var common = selection[0].Tags.Where(t => selection.All(i => i.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))).ToList();
        var editor = new TagEditorViewModel { Suggestions = _tagNames };
        editor.Reset(common);
        var accepted = await _services.Shell.ShowDialogAsync(new DialogDescriptor
        {
            Title = selection.Count == 1
                ? Loc.Get("Library.EditTags.Title")
                : Loc.Format("Library.EditTags.TitleMany", selection.Count),
            Content = new TagEditorView { DataContext = editor, Width = 400 },
            PrimaryText = Loc.Get("Library.EditTags.Save"),
        });
        if (!accepted)
        {
            return;
        }

        editor.AddCommand.Execute(null); // a tag typed but not yet added with Enter
        var comparer = StringComparer.OrdinalIgnoreCase;
        var removed = common.Where(t => !editor.Tags.Contains(t, comparer)).ToHashSet(comparer);
        foreach (var item in selection)
        {
            var tags = item.Tags.Where(t => !removed.Contains(t)).Concat(editor.Tags).Distinct(comparer).ToList();
            if (tags.Count == item.Tags.Count && tags.All(t => item.Tags.Contains(t, comparer)))
            {
                continue;
            }

            if (!await TryAsync(() => _services.Library.SetTagsAsync(item.Id, tags)))
            {
                return;
            }
        }
    }

    [RelayCommand]
    private async Task RemoveSelected()
    {
        var ids = SelectedTiles.Select(t => t.Id).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var confirmed = await _services.Shell.ConfirmAsync(
            Loc.Get("Library.Remove.Title"),
            Loc.Format("Library.Remove.Message", ids.Count),
            Loc.Get("Library.Remove.Confirm"));
        if (confirmed)
        {
            await TryAsync(() => _services.Library.RemoveAsync(ids));
        }
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task Rescan()
    {
        var sources = await _services.Library.GetSourcesAsync(Kind);
        await ScanSourcesAsync(sources);
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task AddFolder() => await AddFolderCoreAsync();

    [RelayCommand]
    private Task ManageFolders() => FolderManagerViewModel.ShowAsync(this, _services);

    private bool CanScan() => !IsScanning && HasScanner;

    /// <summary>Picks a folder, adds it as a source and scans it. Returns true when a folder was added.</summary>
    internal async Task<bool> AddFolderCoreAsync()
    {
        var path = _services.PickFolder();
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        LibrarySource source;
        try
        {
            source = await _services.Library.AddSourceAsync(Kind, path);
        }
        catch (ArgumentException)
        {
            await _services.Shell.ShowMessageAsync(Loc.Get("Library.Folders.OverlapTitle"), Loc.Format("Library.Folders.Overlap", path));
            return false;
        }
        catch (Exception ex)
        {
            _services.Log.Error($"Adding the folder {path} failed.", ex);
            _services.Shell.Notify(Loc.Get("Library.Error.SaveFailed"), NotifySeverity.Error);
            return false;
        }

        await ReloadAllAsync();
        _ = ScanSourcesAsync([source]);
        return true;
    }

    /// <summary>Scans sources one after another, reported in the status bar; announces the totals.</summary>
    internal async Task ScanSourcesAsync(IReadOnlyList<LibrarySource> sources)
    {
        if (IsScanning || sources.Count == 0)
        {
            return;
        }

        IsScanning = true;
        int added = 0, missing = 0, relinked = 0;
        var completed = false;
        using (var task = _services.Shell.BeginTask(_owner, Loc.Format("Library.Scan.TaskTitle", KindName)))
        {
            try
            {
                foreach (var source in sources)
                {
                    var progress = new InlineProgress(p => task.Report(null, Loc.Format("Library.Scan.Found", p.Found)));
                    var result = await _services.Library.ScanAsync(source.Id, progress, _owner.Deactivating);
                    added += result.Added;
                    missing += result.MarkedMissing;
                    relinked += result.Relinked;
                }

                completed = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _services.Log.Error($"Scanning {Kind} folders failed.", ex);
                _services.Shell.Notify(Loc.Get("Library.Error.ScanFailed"), NotifySeverity.Error);
            }
            finally
            {
                IsScanning = false;
            }
        }

        if (completed && !_disposed)
        {
            _services.Shell.Notify(Loc.Format("Library.Scan.Result", added, missing, relinked), NotifySeverity.Success);
        }
    }

    private async Task PatchSelectionAsync(Func<LibraryItem, ItemPatch> patch)
    {
        foreach (var item in SelectedItems)
        {
            if (!await TryAsync(() => _services.Library.UpdateAsync(item.Id, patch(item))))
            {
                return;
            }
        }
    }

    private async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            _services.Log.Error("A library update failed.", ex);
            _services.Shell.Notify(Loc.Get("Library.Error.SaveFailed"), NotifySeverity.Error);
            return false;
        }
    }

    private sealed class InlineProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
