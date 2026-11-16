using System.Globalization;
using System.IO;
using Magpienest.Sdk;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Settings;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Catalogue;

/// <summary>
/// The store's state and actions for the store page (T8.2): the fetched catalogue, its last error, and installing
/// a bundle with one confirmation. Runs on the UI thread; network and file work run in the background.
/// </summary>
internal sealed class StoreService : IDisposable
{
    /// <summary>Config key: the store repository as "owner/name".</summary>
    public const string RepositoryKey = "storeRepository";

    /// <summary>Config key: fetch the catalogue once at every start (default off).</summary>
    public const string CheckAtStartupKey = "checkUpdatesAtStartup";

    /// <summary>Filled in once the user has created the store repository (phase 8 T8.4); empty = not configured.</summary>
    public const string DefaultRepository = "";

    /// <summary>Id of the store's Settings section (T8.2); it carries the update dot.</summary>
    public const string SectionId = "store";

    private readonly IPluginContext _ctx;
    private readonly IShellService _shell;
    private readonly ILocalizationService _localization;
    private readonly ISettingsService _settings;
    private readonly IPluginManager _manager;
    private StoreClient? _client;
    private string? _clientRepository;
    private CancellationTokenSource? _install;
    private bool _raiseQueued;

    public StoreService(IPluginContext ctx, IShellService shell, ISettingsService settings, ILocalizationService localization, IPluginManager manager)
    {
        _ctx = ctx;
        _shell = shell;
        _settings = settings;
        _localization = localization;
        _manager = manager;
    }

    /// <summary>Raised on the UI thread whenever anything below changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The last catalogue fetched, or null.</summary>
    public StoreCatalogue? Catalogue { get; private set; }

    /// <summary>Why the last refresh failed; null after a successful one.</summary>
    public StoreError? LastError { get; private set; }

    public bool IsRefreshing { get; private set; }

    /// <summary>The bundle being installed, or null.</summary>
    public string? InstallingId { get; private set; }

    /// <summary>Every catalogue bundle with its state on this computer.</summary>
    public IReadOnlyList<StoreEntry> Entries =>
        Catalogue is null ? [] : StoreStatus.Of(Catalogue, _manager.Plugins, _ctx.Manifest.ApiVersion);

    /// <summary>Number of installed bundles with an update.</summary>
    public int UpdateCount => Entries.Count(e => e.State == StoreBundleState.UpdateAvailable);

    /// <summary>The UI language, for bundle names and descriptions.</summary>
    public string Language => _localization.CurrentLanguage;

    public bool CheckAtStartup
    {
        get => _ctx.Config.Get(CheckAtStartupKey, false);
        set
        {
            _ctx.Config.Set(CheckAtStartupKey, value);
            Raise();
        }
    }

    /// <summary>Localization key for a store error.</summary>
    public static string MessageKey(StoreError error) => "Catalogue.Error." + error;

    /// <summary>"12.3 MB" or "456 KB".</summary>
    public static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024
            ? Loc.Format("Catalogue.Size.MB", (bytes / (1024d * 1024d)).ToString("0.0", CultureInfo.CurrentCulture))
            : Loc.Format("Catalogue.Size.KB", Math.Max(1, (bytes + 1023) / 1024).ToString(CultureInfo.CurrentCulture));

    /// <summary>Fetches the catalogue at start-up when the user turned that on.</summary>
    public void OnAppStarted()
    {
        if (CheckAtStartup)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>Fetches the catalogue. Never throws; failures end up in <see cref="LastError"/>.</summary>
    public async Task RefreshAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        var client = Client();
        if (client is null)
        {
            Catalogue = null;
            LastError = StoreError.NotConfigured;
            Raise();
            return;
        }

        IsRefreshing = true;
        Raise();
        try
        {
            var catalogue = await client.FetchCatalogueAsync(_ctx.Deactivating);
            foreach (var reason in catalogue.Rejected)
            {
                _ctx.Log.Warn($"Catalogue entry left out. {reason}");
            }

            Catalogue = catalogue;
            LastError = null;
        }
        catch (StoreException ex)
        {
            _ctx.Log.Warn($"Fetching the catalogue failed: {ex.Message}");
            LastError = ex.Code;
        }
        catch (OperationCanceledException)
        {
            // The plugin is being deactivated.
        }
        finally
        {
            IsRefreshing = false;
            Raise();
        }
    }

    /// <summary>Asks once, then downloads and installs the bundle. Never throws.</summary>
    public async Task InstallAsync(StoreEntry entry)
    {
        if (entry.State is not (StoreBundleState.NotInstalled or StoreBundleState.UpdateAvailable))
        {
            return;
        }

        var bundle = entry.Bundle;
        var name = bundle.NameFor(_localization.CurrentLanguage);
        if (_install is not null)
        {
            await _shell.ShowMessageAsync(Loc.Format("Catalogue.Failed.Title", name), Loc.Get(MessageKey(StoreError.Busy)));
            return;
        }

        var client = Client();
        if (client is null)
        {
            await _shell.ShowMessageAsync(Loc.Format("Catalogue.Failed.Title", name), Loc.Get(MessageKey(StoreError.NotConfigured)));
            return;
        }

        var update = entry.State == StoreBundleState.UpdateAvailable;
        if (!await _shell.ConfirmAsync(
                Loc.Format(update ? "Catalogue.Confirm.UpdateTitle" : "Catalogue.Confirm.Title", name),
                ConfirmMessage(entry),
                Loc.Get(update ? "Catalogue.Confirm.UpdateButton" : "Catalogue.Confirm.Button")))
        {
            return;
        }

        _install = CancellationTokenSource.CreateLinkedTokenSource(_ctx.Deactivating);
        InstallingId = bundle.Id;
        Raise();
        var task = _shell.BeginTask(_ctx, Loc.Format("Catalogue.Task.Downloading", name));
        var taskOpen = true;
        void EndTask()
        {
            if (taskOpen)
            {
                taskOpen = false;
                task.Dispose();
            }
        }

        try
        {
            var installer = new BundleInstaller(_manager, client, Path.Combine(_ctx.Environment.CacheDirectory, "downloads"), _ctx.Log);
            var outcome = await installer.InstallAsync(bundle, new ProgressReporter<double>(f => task.Report(f)), _install.Token);
            EndTask();
            if (outcome.Success)
            {
                _shell.Notify(Loc.Format(outcome.RestartRequired ? "Catalogue.InstalledRestart" : "Catalogue.Installed", name), NotifySeverity.Success);
            }
            else
            {
                await _shell.ShowMessageAsync(Loc.Format("Catalogue.Failed.Title", name), Loc.Get(MessageKey(outcome.Error ?? StoreError.InstallFailed)));
            }
        }
        catch (OperationCanceledException)
        {
            EndTask();
            if (!_ctx.Deactivating.IsCancellationRequested)
            {
                _shell.Notify(Loc.Format("Catalogue.Cancelled", name));
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Error($"Installing bundle '{bundle.Id}' failed.", ex);
            EndTask();
            await _shell.ShowMessageAsync(Loc.Format("Catalogue.Failed.Title", name), Loc.Get(MessageKey(StoreError.InstallFailed)));
        }
        finally
        {
            EndTask();
            _install.Dispose();
            _install = null;
            InstallingId = null;
            Raise();
        }
    }

    /// <summary>Stops the running download; nothing is installed.</summary>
    public void CancelInstall() => _install?.Cancel();

    public void Dispose()
    {
        _install?.Cancel();
        _client?.Dispose();
        _client = null;
    }

    private string ConfirmMessage(StoreEntry entry)
    {
        var bundle = entry.Bundle;
        var author = string.IsNullOrWhiteSpace(bundle.Author) ? Loc.Get("Catalogue.Confirm.AuthorUnknown") : bundle.Author;
        var lines = new List<string> { Loc.Format("Catalogue.Confirm.Details", bundle.Version, author, FormatSize(bundle.TotalSize)) };
        if (entry.State == StoreBundleState.UpdateAvailable && entry.InstalledVersion is not null)
        {
            lines.Add(Loc.Format("Catalogue.Confirm.UpdateNote", entry.InstalledVersion, bundle.Version));
        }

        lines.Add(Loc.Get("Catalogue.Confirm.Warning"));
        return string.Join("\n\n", lines);
    }

    private StoreClient? Client()
    {
        var repository = _ctx.Config.Get(RepositoryKey, DefaultRepository);
        if (_client is not null && repository == _clientRepository)
        {
            return _client;
        }

        _client?.Dispose();
        _client = null;
        _clientRepository = repository;
        var policy = StorePolicy.ForRepository(repository);
        if (policy is not null)
        {
            _client = new StoreClient(policy, _ctx.Environment.AppVersion);
        }

        return _client;
    }

    /// <summary>
    /// Called after the catalogue or the installed plugins changed: the store's Settings section, and so the
    /// Settings navigation entry, shows a dot while an installed bundle has an update.
    /// </summary>
    public void Raise()
    {
        _settings.SetBadge(_ctx, SectionId, UpdateCount > 0);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Coalesces <see cref="Raise"/> calls and runs one after the current kernel operation.</summary>
    public void ScheduleRaise()
    {
        if (_raiseQueued)
        {
            return;
        }

        _raiseQueued = true;
        _ctx.Ui.Post(() =>
        {
            _raiseQueued = false;
            if (!_ctx.Deactivating.IsCancellationRequested)
            {
                Raise();
            }
        });
    }
}
