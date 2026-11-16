using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Thumbnails;

namespace Magpienest.Plugins.Game;

/// <summary>The "Games" page: PC games added by hand or imported from a folder, started with one click.</summary>
[Requires(typeof(IShellService), typeof(ILibraryService), typeof(ILibraryBrowserFactory), typeof(IThumbnailService))]
public sealed class GamePlugin : Plugin
{
    /// <summary>Kind and page id.</summary>
    public const string Kind = "game";

    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var shell = ctx.Get<IShellService>();
        var library = ctx.Get<ILibraryService>();
        var browsers = ctx.Get<ILibraryBrowserFactory>();
        var tracker = new PlaySessionTracker(
            new ProcessProbe(),
            new StopwatchClock(),
            (id, seconds) => library.UpdateAsync(id, new ItemPatch { AddTimeSpentSeconds = seconds }),
            Environment.ProcessPath,
            ctx.Log);
        var launcher = new GameLauncher(library, shell, ctx.Log, tracker);
        var games = new GameItems(ctx, shell, library, launcher, tracker);

        // The poll loop runs only while a game is being followed; it starts when a session does.
        var pump = new PlaySessionPump(tracker, ctx.Log, ctx.Deactivating);
        Action kick = pump.Kick;
        tracker.SessionsChanged += kick;
        ctx.Track(() => tracker.SessionsChanged -= kick);
        ctx.On<AppShuttingDown>(_ => SaveRunningGames(ctx, tracker));
        ctx.Track(() => SaveRunningGames(ctx, tracker));

        if (GameCommandLine.ImportFolder(ctx.Environment.CommandLineArgs) is { } importFolder)
        {
            var started = false;
            void Begin()
            {
                if (!started)
                {
                    started = true;
                    _ = games.ImportFolderAsync(importFolder);
                }
            }

            ctx.On<AppStarted>(_ => Begin());
            if (ctx.Environment.IsStarted)
            {
                Begin();
            }
        }

        // Games are never found by scanning folders; they are added by hand or imported on request.
        library.RegisterKind(ctx, new ItemKindDescriptor
        {
            Id = Kind,
            DisplayNameKey = "Game.Kind",
            Scanner = null,
        });
        ctx.Get<IThumbnailService>().RegisterProvider(ctx, Kind, new GameThumbnails(ctx.Get<IThumbnailService>()));

        shell.AddPage(ctx, new PageDescriptor
        {
            Id = Kind,
            TitleKey = "Game.Page.Title",
            Glyph = "",
            Order = 400,
            CreateView = () => browsers.CreateBrowser(ctx, new BrowserOptions
            {
                Kind = Kind,
                OpenItem = request => launcher.PlayAsync(request.Item),
                TileAspectRatio = 0.7,
                ThumbnailStretch = Stretch.UniformToFill,
                ShowFolderTree = false,
                ShowStatus = true,
                AddItem = games.ShowAddMenuAsync,
                Sorts = [ItemSort.Title, ItemSort.AddedAt, ItemSort.LastOpenedAt, ItemSort.TimeSpent, ItemSort.Rating],
                DefaultSort = ItemSort.Title,
                Subtitle = games.Subtitle,
                Commands = games.Commands,
                CreateDetails = item => GameDetails.Create(item, launcher, tracker, games.EditAsync),
            }),
        });
    }

    /// <summary>
    /// Stores the unsaved play time. This is the one place that waits for a background task: it runs while the
    /// application exits, and the library closes right after, so a write that is only started would be lost.
    /// </summary>
    private static void SaveRunningGames(IPluginContext ctx, PlaySessionTracker tracker)
    {
        if (!tracker.HasSessions)
        {
            return;
        }

        try
        {
            if (!Task.Run(tracker.FlushAllAsync).Wait(TimeSpan.FromSeconds(3)))
            {
                ctx.Log.Warn("Saving the play time took too long.");
            }
        }
        catch (Exception ex)
        {
            ctx.Log.Warn("Saving the play time failed.", ex);
        }
    }
}

/// <summary>Tile texts, the add menu and the context-menu commands.</summary>
internal sealed class GameItems(IPluginContext ctx, IShellService shell, ILibraryService library, GameLauncher launcher, PlaySessionTracker tracker)
{
    /// <summary>"Running" while the game is followed, else the play time.</summary>
    public string? Subtitle(LibraryItem item) =>
        tracker.IsTracking(item.Id) ? Loc.Get("Game.Running") : GameFormat.PlayTime(item.TimeSpentSeconds, item.OpenCount);

    public IReadOnlyList<BrowserCommand> Commands =>
    [
        new BrowserCommand("Game.Command.Play", "",
            items => launcher.PlayAsync(items[0]),
            items => items.Count == 1),
        new BrowserCommand("Game.Command.Edit", "",
            items => EditAsync(items[0]),
            items => items.Count == 1),
        new BrowserCommand("Game.Command.OpenFolder", "",
            items => launcher.OpenFolderAsync(GameExtra.EffectiveInstallDir(items[0])),
            items => items.Count == 1),
        new BrowserCommand("Game.Command.SetCover", "", SetCoverAsync, items => items.Count == 1),
        new BrowserCommand("Game.Command.ResetCover", "",
            items => UpdateAllAsync(items.Where(i => i.CoverPath is not null), new ItemPatch { ClearCover = true }),
            items => items.Any(i => i.CoverPath is not null)),
    ];

    /// <summary>The toolbar "Add" button: a small menu with "Add game…" and "Scan folder…".</summary>
    public Task ShowAddMenuAsync()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(MenuEntry("Game.Add.Game", "", () => AddAsync()));
        menu.Items.Add(MenuEntry("Game.Add.Scan", "", ScanFolderAsync));
        menu.IsOpen = true;
        return Task.CompletedTask;
    }

    private MenuItem MenuEntry(string key, string glyph, Func<Task> run)
    {
        var item = new MenuItem
        {
            Header = Loc.Get(key),
            Icon = new TextBlock { Text = glyph, FontSize = 14 },
        };
        ((TextBlock)item.Icon).SetResourceReference(TextBlock.FontFamilyProperty, "SymbolThemeFontFamily");
        item.Click += (_, _) => _ = GuardAsync(run);
        return item;
    }

    private async Task GuardAsync(Func<Task> run)
    {
        try
        {
            await run();
        }
        catch (Exception ex)
        {
            ctx.Log.Error("A games action failed.", ex);
            shell.Notify(Loc.Get("Game.Error.Failed"), NotifySeverity.Error);
        }
    }

    // ----- add and edit ---------------------------------------------------------------------------------------

    /// <summary>Shows the Add game dialog and adds the game.</summary>
    public async Task AddAsync()
    {
        var editor = new GameEditorViewModel(ctx.Log);
        if (!await ShowEditorAsync("Game.Editor.AddTitle", "Game.Editor.Add", editor))
        {
            return;
        }

        try
        {
            await library.AddAsync(new NewItem
            {
                Kind = GamePlugin.Kind,
                Path = editor.Executable.Trim(),
                Title = editor.Title.Trim(),
                Extra = editor.ToExtra(),
            });
        }
        catch (InvalidOperationException ex)
        {
            ctx.Log.Warn("Adding a game failed.", ex);
            shell.Notify(Loc.Get("Game.Error.AlreadyAdded"), NotifySeverity.Warning);
        }
    }

    /// <summary>Shows the Edit game dialog and saves the changes.</summary>
    /// <param name="item">The game.</param>
    public async Task EditAsync(LibraryItem item)
    {
        var editor = GameEditorViewModel.For(item, ctx.Log);
        if (!await ShowEditorAsync("Game.Editor.EditTitle", "Game.Editor.Save", editor))
        {
            return;
        }

        var path = editor.Executable.Trim();
        try
        {
            await library.UpdateAsync(item.Id, new ItemPatch
            {
                Title = editor.Title.Trim(),
                Path = string.Equals(path, item.Path, StringComparison.OrdinalIgnoreCase) ? null : path,
                Extra = editor.ToExtra(),
            });
        }
        catch (InvalidOperationException ex)
        {
            ctx.Log.Warn("Saving a game failed.", ex);
            shell.Notify(Loc.Get("Game.Error.AlreadyAdded"), NotifySeverity.Warning);
        }
    }

    private Task<bool> ShowEditorAsync(string titleKey, string primaryKey, GameEditorViewModel editor) =>
        shell.ShowDialogAsync(new DialogDescriptor
        {
            Title = Loc.Get(titleKey),
            Content = new GameEditorView { DataContext = editor },
            PrimaryText = Loc.Get(primaryKey),
            CanAccept = () => editor.CanAccept,
            Width = 560,
        });

    // ----- scan a folder --------------------------------------------------------------------------------------

    /// <summary>Picks a folder, analyses it in the background and shows the import dialog.</summary>
    public async Task ScanFolderAsync()
    {
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.Get("Game.Scan.ChooseFolder"),
            Multiselect = false,
        };
        if (picker.ShowDialog(shell.MainWindow) != true)
        {
            return;
        }

        var root = picker.FolderName;
        IReadOnlyList<GameCandidate> candidates;
        using (var task = shell.BeginTask(ctx, Loc.Get("Game.Scan.Progress")))
        {
            task.Report(null);
            var existing = await library.QueryAsync(new ItemQuery { Kind = GamePlugin.Kind, IncludeMissing = true }, ctx.Deactivating);
            var index = GameLibraryIndex.From(existing);
            candidates = await Task.Run(() => new GameFolderScanner().Analyze(root, index, ctx.Deactivating), ctx.Deactivating);
        }

        if (candidates.Count == 0)
        {
            shell.Notify(Loc.Format("Game.Scan.Nothing", root), NotifySeverity.Warning);
            return;
        }

        var model = new GameImportViewModel(root, candidates);
        var accepted = await shell.ShowDialogAsync(new DialogDescriptor
        {
            Title = Loc.Get("Game.Import.Title"),
            Content = new GameImportView { DataContext = model },
            PrimaryText = Loc.Get("Game.Import.Import"),
            CanAccept = () => model.CanAccept,
            Width = 860,
        });
        if (accepted)
        {
            await ImportAsync(model.Rows.Where(r => r.WillImport).ToList());
        }
    }

    private async Task ImportAsync(IReadOnlyList<GameImportRow> rows)
    {
        var added = 0;
        foreach (var row in rows)
        {
            try
            {
                await library.AddAsync(new NewItem
                {
                    Kind = GamePlugin.Kind,
                    Path = row.SelectedExecutable!.FullPath,
                    Title = row.Title.Trim(),
                    Extra = LibraryItemExtensions.SerializeExtra(new GameExtra { InstallDir = row.Candidate.Folder }),
                });
                added++;
            }
            catch (InvalidOperationException ex)
            {
                // Added in the meantime (for example by a second import); skip it.
                ctx.Log.Warn($"Importing {row.Candidate.Folder} failed.", ex);
            }
        }

        shell.Notify(Loc.Format("Game.Import.Done", added), added > 0 ? NotifySeverity.Success : NotifySeverity.Warning);
    }

    /// <summary>
    /// <c>--import-games &lt;folder&gt;</c>: analyses the folder in the background and adds every game that is ready,
    /// with its best program and the folder name as title. No dialog.
    /// </summary>
    /// <param name="folder">The folder of game folders.</param>
    public async Task ImportFolderAsync(string folder)
    {
        try
        {
            var root = Path.GetFullPath(folder);
            var existing = await library.QueryAsync(new ItemQuery { Kind = GamePlugin.Kind, IncludeMissing = true }, ctx.Deactivating);
            var index = GameLibraryIndex.From(existing);
            var candidates = await Task.Run(() => new GameFolderScanner().Analyze(root, index, ctx.Deactivating), ctx.Deactivating);
            var added = 0;
            foreach (var item in GameCommandLine.SelectImports(candidates))
            {
                try
                {
                    await library.AddAsync(item);
                    added++;
                }
                catch (InvalidOperationException ex)
                {
                    ctx.Log.Warn($"Importing {item.Path} failed.", ex);
                }
            }

            ctx.Log.Info($"--import-games '{root}': {added} game(s) added, {candidates.Count} folder(s) found.");
        }
        catch (OperationCanceledException)
        {
            // Deactivating.
        }
        catch (Exception ex)
        {
            ctx.Log.Error($"--import-games '{folder}' failed.", ex);
        }
    }

    // ----- covers ---------------------------------------------------------------------------------------------

    private async Task SetCoverAsync(IReadOnlyList<LibraryItem> items)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("Game.Cover.Title"),
            Filter = Loc.Get("Game.Cover.Filter") + "|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(shell.MainWindow) == true)
        {
            await UpdateAllAsync([items[0]], new ItemPatch { SetCoverFromFile = dialog.FileName });
        }
    }

    private async Task UpdateAllAsync(IEnumerable<LibraryItem> items, ItemPatch patch)
    {
        foreach (var item in items)
        {
            try
            {
                await library.UpdateAsync(item.Id, patch);
            }
            catch (Exception ex)
            {
                ctx.Log.Warn("Updating a game failed.", ex);
                shell.Notify(Loc.Get("Game.Error.Failed"), NotifySeverity.Error);
            }
        }
    }
}
