using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Thumbnails;

namespace Magpienest.Plugins.Library;

/// <summary>The "scan at startup" setting and the background scan it triggers 3 s after <see cref="AppStarted"/>.</summary>
internal sealed class StartupScan(IPluginContext ctx, ILibraryService library, IShellService shell)
{
    public const string ConfigKey = "startupScan";
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    public void OnAppStarted(AppStarted evt)
    {
        if (ctx.Config.Get(ConfigKey, true))
        {
            _ = RunAsync(ctx.Deactivating);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Delay, ct);
            var sources = new List<(LibrarySource Source, string KindKey)>();
            foreach (var kind in library.Kinds.Where(k => k.Scanner is not null))
            {
                foreach (var source in await library.GetSourcesAsync(kind.Id))
                {
                    sources.Add((source, kind.DisplayNameKey));
                }
            }

            if (sources.Count == 0)
            {
                return;
            }

            int added = 0, missing = 0, relinked = 0;
            using var task = shell.BeginTask(ctx, Loc.Get("Library.Settings.StartupScanTask"));
            foreach (var (source, _) in sources)
            {
                ct.ThrowIfCancellationRequested();
                var result = await library.ScanAsync(source.Id, null, ct);
                added += result.Added;
                missing += result.MarkedMissing;
                relinked += result.Relinked;
            }

            if (added + missing + relinked > 0)
            {
                shell.Notify(Loc.Format("Library.Scan.Result", added, missing, relinked), NotifySeverity.Success);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ctx.Log.Error("The startup scan failed.", ex);
        }
    }
}

/// <summary>One kind in the "Remove missing items" list.</summary>
internal sealed class MissingKindRow(ItemKindDescriptor kind, LibrarySettingsViewModel owner)
{
    public string Name => Loc.Get(kind.DisplayNameKey);

    public IAsyncRelayCommand RemoveMissingCommand { get; } = new AsyncRelayCommand(() => owner.RemoveMissingAsync(kind));
}

/// <summary>Settings section "library".</summary>
internal sealed partial class LibrarySettingsViewModel : ObservableObject
{
    private readonly IPluginContext _ctx;
    private readonly ILibraryService _library;
    private readonly IThumbnailService _thumbnails;
    private readonly IShellService _shell;

    public LibrarySettingsViewModel(IPluginContext ctx, ILibraryService library, IThumbnailService thumbnails, IShellService shell)
    {
        _ctx = ctx;
        _library = library;
        _thumbnails = thumbnails;
        _shell = shell;
        ScanOnStartup = ctx.Config.Get(StartupScan.ConfigKey, true);
        foreach (var kind in library.Kinds)
        {
            Kinds.Add(new MissingKindRow(kind, this));
        }

        UpdateCacheSize();
    }

    [ObservableProperty]
    public partial bool ScanOnStartup { get; set; }

    [ObservableProperty]
    public partial string CacheSizeText { get; private set; } = "";

    public ObservableCollection<MissingKindRow> Kinds { get; } = [];

    public bool HasKinds => Kinds.Count > 0;

    partial void OnScanOnStartupChanged(bool value) => _ctx.Config.Set(StartupScan.ConfigKey, value);

    [RelayCommand]
    private async Task ClearCache()
    {
        var confirmed = await _shell.ConfirmAsync(
            Loc.Get("Library.Settings.ClearCacheTitle"),
            Loc.Get("Library.Settings.ClearCacheMessage"),
            Loc.Get("Library.Settings.ClearCacheConfirm"));
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _thumbnails.ClearCacheAsync();
            _shell.Notify(Loc.Get("Library.Settings.CacheCleared"), NotifySeverity.Success);
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("Clearing the thumbnail cache failed.", ex);
            _shell.Notify(Loc.Get("Library.Error.SaveFailed"), NotifySeverity.Error);
        }

        UpdateCacheSize();
    }

    internal async Task RemoveMissingAsync(ItemKindDescriptor kind)
    {
        var items = await _library.QueryAsync(new ItemQuery { Kind = kind.Id, IncludeMissing = true });
        var missing = items.Where(i => i.IsMissing).Select(i => i.Id).ToList();
        var name = Loc.Get(kind.DisplayNameKey);
        if (missing.Count == 0)
        {
            await _shell.ShowMessageAsync(Loc.Get("Library.Settings.RemoveMissingTitle"), Loc.Format("Library.Settings.NoMissing", name));
            return;
        }

        var confirmed = await _shell.ConfirmAsync(
            Loc.Get("Library.Settings.RemoveMissingTitle"),
            Loc.Format("Library.Settings.RemoveMissingMessage", missing.Count, name),
            Loc.Get("Library.Remove.Confirm"));
        if (confirmed)
        {
            await _library.RemoveAsync(missing);
        }
    }

    private void UpdateCacheSize()
    {
        long bytes = 0;
        try
        {
            var cache = _ctx.Environment.CacheDirectory;
            if (Directory.Exists(cache))
            {
                foreach (var file in Directory.EnumerateFiles(cache, "thumbnails.db*"))
                {
                    bytes += new FileInfo(file).Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        CacheSizeText = Loc.Format("Library.Settings.CacheSize", Browser.DetailsViewModel.FormatSize(bytes));
    }
}

/// <summary>View of the "library" settings section.</summary>
internal sealed partial class LibrarySettingsView : UserControl
{
    public LibrarySettingsView() => InitializeComponent();
}
