using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Catalogue;

/// <summary>The "store" Settings section. Shows <see cref="StoreService"/>; decides nothing itself.</summary>
internal sealed partial class StorePageViewModel : ObservableObject
{
    private readonly IPluginContext _ctx;
    private readonly StoreService _service;
    private readonly IShellService _shell;
    private readonly IPluginManager _manager;
    private readonly Dictionary<string, StoreCardViewModel> _cards = new(StringComparer.Ordinal);

    public StorePageViewModel(IPluginContext ctx, StoreService service, IShellService shell, IPluginManager manager)
    {
        _ctx = ctx;
        _service = service;
        _shell = shell;
        _manager = manager;
        _service.Changed += OnChanged;
        ctx.Track(() => _service.Changed -= OnChanged);
        Update();
    }

    public ObservableCollection<StoreCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasStatus { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial bool ShowRestartBar { get; set; }

    /// <summary>A newer Magpienest is listed: the card at the top.</summary>
    [ObservableProperty]
    public partial bool HasAppUpdate { get; set; }

    [ObservableProperty]
    public partial string AppTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AppNotes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AppDetails { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AppActionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUpdatingApp { get; set; }

    [ObservableProperty]
    public partial bool HasAppPage { get; set; }

    public bool CheckAtStartup
    {
        get => _service.CheckAtStartup;
        set => _service.CheckAtStartup = value;
    }

    /// <summary>Called when the section is shown: fetches the catalogue.</summary>
    public void OnShown() => _ = _service.RefreshAsync();

    /// <summary>Rebuilds every text and card from the service.</summary>
    public void Update()
    {
        IsRefreshing = _service.IsRefreshing;
        var key = StoreCards.StatusKey(_service.IsRefreshing, _service.LastError, _service.Catalogue is not null, _service.Catalogue?.Bundles.Count ?? 0);
        StatusText = key is null ? string.Empty : Loc.Get(key);
        HasStatus = key is not null;
        ShowRestartBar = _manager.RestartRequired;
        OnPropertyChanged(nameof(CheckAtStartup));

        var app = _service.AppUpdate;
        HasAppUpdate = app is not null;
        IsUpdatingApp = _service.IsUpdatingApp;
        if (app is not null)
        {
            AppTitle = Loc.Format("Catalogue.App.CardTitle", app.Version);
            AppNotes = app.NotesFor(_service.Language);
            AppDetails = IsUpdatingApp
                ? Loc.Format("Catalogue.App.Progress", Math.Round(_service.AppUpdateProgress * 100).ToString("0", System.Globalization.CultureInfo.CurrentCulture))
                : Loc.Format("Catalogue.App.Details", _service.AppVersion, StoreService.FormatSize(app.Package.Size));
            AppActionText = Loc.Get(IsUpdatingApp ? "Catalogue.Page.Action.Cancel" : "Catalogue.App.Update");
            HasAppPage = app.Page is not null;
        }

        var language = _service.Language;
        var entries = _service.Entries;
        foreach (var entry in entries)
        {
            if (!_cards.TryGetValue(entry.Bundle.Id, out var card))
            {
                card = new StoreCardViewModel(Act);
                _cards[entry.Bundle.Id] = card;
            }

            card.Update(entry, language, _service.InstallingId);
        }

        var ids = entries.Select(e => e.Bundle.Id).ToList();
        foreach (var id in _cards.Keys.Except(ids).ToList())
        {
            _cards.Remove(id);
        }

        var sorted = ids.Select(id => _cards[id]).ToList();
        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (!sorted.Contains(Cards[i]))
            {
                Cards.RemoveAt(i);
            }
        }

        for (var i = 0; i < sorted.Count; i++)
        {
            var index = Cards.IndexOf(sorted[i]);
            if (index < 0)
            {
                Cards.Insert(i, sorted[i]);
            }
            else if (index != i)
            {
                Cards.Move(index, i);
            }
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => _service.RefreshAsync();

    [RelayCommand]
    private void UpdateApp()
    {
        if (_service.IsUpdatingApp)
        {
            _service.CancelAppUpdate();
        }
        else
        {
            _ = _service.UpdateAppAsync();
        }
    }

    [RelayCommand]
    private void OpenAppPage()
    {
        if (_service.AppUpdate?.Page is not { } page)
        {
            return;
        }

        try
        {
            using var started = Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn($"Opening '{page}' failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RestartNow()
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The path of the running exe is unknown.");
            var appArguments = RestartCommand.AppArguments(Environment.ProcessId, _ctx.Environment.DataDirectory, _ctx.Environment.CommandLineArgs);
            using var started = Process.Start(RestartCommand.Create(exe, appArguments))
                ?? throw new InvalidOperationException("The new process did not start.");
            _ctx.Log.Info("Restarting Magpienest.");
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("Restarting Magpienest failed.", ex);
            _shell.Notify(Loc.Format("Catalogue.Page.RestartFailed", ex.Message), NotifySeverity.Error);
            return;
        }

        _ctx.Environment.RequestShutdown();
    }

    private void Act(StoreCardViewModel card)
    {
        if (card.Entry is not { } entry)
        {
            return;
        }

        if (card.Action == StoreCardAction.Cancel)
        {
            _service.CancelInstall();
        }
        else
        {
            _ = _service.InstallAsync(entry);
        }
    }

    private void OnChanged(object? sender, EventArgs e) => Update();
}

/// <summary>One bundle on the store page.</summary>
internal sealed partial class StoreCardViewModel : ObservableObject
{
    private readonly Action<StoreCardViewModel> _act;

    public StoreCardViewModel(Action<StoreCardViewModel> act) => _act = act;

    public StoreEntry? Entry { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial StoreCardAction Action { get; set; }

    [ObservableProperty]
    public partial string ActionText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ActCommand))]
    public partial bool CanAct { get; set; }

    /// <summary>The primary look: Install and Update.</summary>
    [ObservableProperty]
    public partial bool IsPrimary { get; set; }

    public void Update(StoreEntry entry, string language, string? installingId)
    {
        Entry = entry;
        var bundle = entry.Bundle;
        Name = bundle.NameFor(language);
        Description = bundle.DescriptionFor(language);
        var author = string.IsNullOrWhiteSpace(bundle.Author) ? Loc.Get("Catalogue.Confirm.AuthorUnknown") : bundle.Author;
        DetailsText = entry.State == StoreBundleState.UpdateAvailable && entry.InstalledVersion is not null
            ? Loc.Format("Catalogue.Page.DetailsUpdate", bundle.Version, StoreService.FormatSize(bundle.TotalSize), author, entry.InstalledVersion)
            : Loc.Format("Catalogue.Page.Details", bundle.Version, StoreService.FormatSize(bundle.TotalSize), author);
        Action = StoreCards.ActionFor(entry.State, installingId == bundle.Id);
        ActionText = Loc.Get(StoreCards.ButtonKey(Action));
        CanAct = StoreCards.CanAct(Action, installingId is not null && installingId != bundle.Id);
        IsPrimary = Action is StoreCardAction.Install or StoreCardAction.Update;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Act() => _act(this);
}
