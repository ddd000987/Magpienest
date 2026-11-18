using System.Globalization;
using System.IO;
using Magpienest.Sdk;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Settings;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Catalogue;

/// <summary>
/// The store's state and actions for the store page: the fetched catalogue, its last error, and installing
/// a bundle with one confirmation. Runs on the UI thread; network and file work run in the background.
/// </summary>
internal sealed class StoreService : IDisposable
{
    /// <summary>Config key: the store repository as "owner/name".</summary>
    public const string RepositoryKey = "storeRepository";

    /// <summary>Config key: fetch the catalogue once at every start, for app and plugin updates (default on).</summary>
    public const string CheckAtStartupKey = "checkUpdatesAtStartup";

    /// <summary>The official store repository (owner/name); a config value under <see cref="RepositoryKey"/> overrides it.</summary>
    public const string DefaultRepository = "ddd000987/Magpienest-Store";

    /// <summary>Id of the store's Settings section; it carries the update dot.</summary>
    public const string SectionId = "store";

    private readonly IPluginContext _ctx;
    private readonly IShellService _shell;
    private readonly ILocalizationService _localization;
    private readonly ISettingsService _settings;
    private readonly IPluginManager _manager;
    private StoreClient? _client;
    private string? _clientRepository;
    private readonly AppUpdater _updater;
    private CancellationTokenSource? _install;
    private CancellationTokenSource? _appUpdate;
    private bool _raiseQueued;
    private bool _appUpdateAnnounced;

    public StoreService(IPluginContext ctx, IShellService shell, ISettingsService settings, ILocalizationService localization, IPluginManager manager)
    {
        _ctx = ctx;
        _shell = shell;
        _settings = settings;
        _localization = localization;
        _manager = manager;
        _updater = new AppUpdater(ctx.Environment, ctx.Log);
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

    /// <summary>The newer Magpienest release the catalogue lists, or null.</summary>
    public CatalogueApp? AppUpdate => _updater.NewerThanRunning(Catalogue);

    /// <summary>The running version.</summary>
    public string AppVersion => _updater.CurrentVersion;

    /// <summary>True while the new Magpienest is being downloaded and unpacked.</summary>
    public bool IsUpdatingApp => _appUpdate is not null;

    /// <summary>Downloaded share of the new Magpienest (0 to 1) while <see cref="IsUpdatingApp"/>.</summary>
    public double AppUpdateProgress { get; private set; }

    /// <summary>The UI language, for bundle names and descriptions.</summary>
    public string Language => _localization.CurrentLanguage;

    public bool CheckAtStartup
    {
        get => _ctx.Config.Get(CheckAtStartupKey, true);
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

    /// <summary>Reports how the last app update ended, then fetches the catalogue unless the user turned that off.</summary>
    public void OnAppStarted()
    {
        if (_updater.TakeReport() is { } report)
        {
            if (report.Success)
            {
                _ctx.Log.Info($"Updated to Magpienest {report.Version}.");
                _shell.Notify(Loc.Format("Catalogue.App.Updated", report.Version), NotifySeverity.Success);
            }
            else
            {
                _ctx.Log.Warn($"Updating to Magpienest {report.Version} failed: {report.Error}");
                _ = _shell.ShowMessageAsync(Loc.Format("Catalogue.App.FailedTitle", report.Version), Loc.Get("Catalogue.App.Undone"));
            }
        }

        // Smoke tests and screenshots never contact the network.
        var unattended = _ctx.Environment.CommandLineArgs.Any(a =>
            string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--screenshot", StringComparison.OrdinalIgnoreCase));
        if (CheckAtStartup && !unattended)
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

        AnnounceAppUpdate();
    }

    /// <summary>Downloads the new Magpienest, then closes this one so it can be replaced. Never throws.</summary>
    public async Task UpdateAppAsync()
    {
        if (AppUpdate is not { } app || _appUpdate is not null)
        {
            return;
        }

        var title = Loc.Format("Catalogue.App.FailedTitle", app.Version);
        if (_install is not null)
        {
            await _shell.ShowMessageAsync(title, Loc.Get(MessageKey(StoreError.Busy)));
            return;
        }

        var client = Client();
        if (client is null)
        {
            await _shell.ShowMessageAsync(title, Loc.Get(MessageKey(StoreError.NotConfigured)));
            return;
        }

        _appUpdate = CancellationTokenSource.CreateLinkedTokenSource(_ctx.Deactivating);
        AppUpdateProgress = 0;
        Raise();
        var task = _shell.BeginTask(_ctx, Loc.Format("Catalogue.App.Downloading", app.Version));
        var size = Math.Max(1, app.Package.Size);
        var lastPercent = -1;
        var progress = new ProgressReporter<long>(bytes =>
        {
            var fraction = Math.Clamp(bytes / (double)size, 0, 1);
            var percent = (int)(fraction * 100);
            if (percent == Interlocked.Exchange(ref lastPercent, percent))
            {
                return;
            }

            task.Report(fraction);
            _ctx.Ui.Post(() =>
            {
                AppUpdateProgress = fraction;
                Changed?.Invoke(this, EventArgs.Empty);
            });
        });

        string? message = null;
        try
        {
            var (outcome, exe) = await _updater.PrepareAsync(app, client, progress, _appUpdate.Token);
            task.Dispose();
            switch (outcome)
            {
                case AppUpdateOutcome.Ready:
                    _updater.Launch(exe!);
                    return;
                case AppUpdateOutcome.NotWritable:
                    message = Loc.Get("Catalogue.App.NotWritable");
                    break;
                default:
                    message = Loc.Get("Catalogue.App.BadPackage");
                    break;
            }
        }
        catch (StoreException ex)
        {
            _ctx.Log.Warn($"Downloading Magpienest {app.Version} failed: {ex.Message}");
            message = Loc.Get(MessageKey(ex.Code));
        }
        catch (OperationCanceledException)
        {
            if (!_ctx.Deactivating.IsCancellationRequested)
            {
                _shell.Notify(Loc.Format("Catalogue.App.Cancelled", app.Version));
            }
        }
        catch (Exception ex)
        {
            _ctx.Log.Error($"Updating to Magpienest {app.Version} failed.", ex);
            message = Loc.Get("Catalogue.App.BadPackage");
        }
        finally
        {
            task.Dispose();
            _appUpdate?.Dispose();
            _appUpdate = null;
            Raise();
        }

        if (message is not null)
        {
            await _shell.ShowMessageAsync(title, message + "\n\n" + Loc.Get("Catalogue.App.ByHand"));
        }
    }

    /// <summary>Stops the running app download; nothing is changed.</summary>
    public void CancelAppUpdate() => _appUpdate?.Cancel();

    /// <summary>Once per run: a toast with an "Update" button when a newer Magpienest is listed.</summary>
    private void AnnounceAppUpdate()
    {
        if (_appUpdateAnnounced || AppUpdate is not { } app)
        {
            return;
        }

        _appUpdateAnnounced = true;
        var notes = app.NotesFor(Language);
        var text = Loc.Format("Catalogue.App.Available", app.Version);
        _shell.Notify(string.IsNullOrWhiteSpace(notes) ? text : text + "\n" + notes, NotifySeverity.Info,
                      Loc.Get("Catalogue.App.Update"), () => _ = UpdateAppAsync());
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
        if (_install is not null || _appUpdate is not null)
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
        _appUpdate?.Cancel();
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
        _settings.SetBadge(_ctx, SectionId, UpdateCount > 0 || AppUpdate is not null);
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
