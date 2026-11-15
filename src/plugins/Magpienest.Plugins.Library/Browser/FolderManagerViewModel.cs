using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>One row of the folder management dialog.</summary>
internal sealed class SourceRow(LibrarySource source, FolderManagerViewModel owner)
{
    public LibrarySource Source { get; } = source;

    public string Path => Source.Path;

    public string CountText => Loc.Format("Library.Folders.ItemCount", Source.ItemCount.ToString("N0", CultureInfo.CurrentCulture));

    public string LastScanText => Source.LastScanAt is { } at
        ? Loc.Format("Library.Folders.LastScan", at.LocalDateTime.ToString("g", CultureInfo.CurrentCulture))
        : Loc.Get("Library.Folders.NeverScanned");

    public IAsyncRelayCommand RemoveCommand { get; } = new AsyncRelayCommand(() => owner.RemoveAsync(source));

    public IAsyncRelayCommand RescanCommand { get; } = new AsyncRelayCommand(() => owner.RescanAsync(source));
}

/// <summary>The folder management dialog: sources of one kind, add, remove, rescan.</summary>
internal sealed partial class FolderManagerViewModel : ObservableObject
{
    private readonly BrowserViewModel _browser;
    private readonly BrowserServices _services;

    private FolderManagerViewModel(BrowserViewModel browser, BrowserServices services)
    {
        _browser = browser;
        _services = services;
    }

    public ObservableCollection<SourceRow> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    public static async Task ShowAsync(BrowserViewModel browser, BrowserServices services)
    {
        var vm = new FolderManagerViewModel(browser, services);
        await vm.LoadAsync();
        await services.Shell.ShowDialogAsync(new DialogDescriptor
        {
            Title = Loc.Get("Library.Folders.Title"),
            Content = new FolderManagerView { DataContext = vm },
            PrimaryText = Loc.Get("Library.Folders.Close"),
            CancelText = string.Empty,
            Width = 640,
        });
    }

    internal async Task RemoveAsync(LibrarySource source)
    {
        var confirmed = await _services.Shell.ConfirmAsync(
            Loc.Get("Library.Folders.RemoveTitle"),
            Loc.Format("Library.Folders.RemoveMessage", source.Path, source.ItemCount),
            Loc.Get("Library.Folders.RemoveConfirm"));
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _services.Library.RemoveSourceAsync(source.Id);
        }
        catch (Exception ex)
        {
            _services.Log.Error($"Removing the folder {source.Path} failed.", ex);
            _services.Shell.Notify(Loc.Get("Library.Error.SaveFailed"), NotifySeverity.Error);
        }

        await LoadAsync();
    }

    internal async Task RescanAsync(LibrarySource source)
    {
        await _browser.ScanSourcesAsync([source]);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        if (await _browser.AddFolderCoreAsync())
        {
            await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        IReadOnlyList<LibrarySource> sources;
        try
        {
            sources = await _services.Library.GetSourcesAsync(_browser.Kind);
        }
        catch (Exception ex)
        {
            _services.Log.Error("Listing library folders failed.", ex);
            sources = [];
        }

        Rows.Clear();
        foreach (var source in sources)
        {
            Rows.Add(new SourceRow(source, this));
        }

        IsEmpty = Rows.Count == 0;
    }
}
