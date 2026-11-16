using System.Reflection;
using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>Plugin lifecycle. UI thread only.</summary>
internal sealed class PluginHost : IPluginManager
{
    private const int MaxPasses = 1000;

    private static readonly HashSet<Type> KernelServiceTypes =
        [typeof(IPluginManager), typeof(IAppEnvironment), typeof(IUiDispatcher)];

    private readonly IReadOnlyList<IPluginSource> _sources;
    private readonly ILog _log;
    private readonly KernelStateStore _state;
    private readonly PluginConfigStore _configs;
    private readonly List<PluginEntry> _entries = [];
    private readonly HashSet<PluginEntry> _deactivating = [];
    private readonly bool _safeMode;
    private readonly string _pluginsRoot;
    private int _installing;
    private bool _started;
    private bool _shutDown;
    private bool _reconciling;
    private bool _dirty;
    private int _activationCounter;

    public PluginHost(IUiDispatcher ui, AppEnvironment environment, IReadOnlyList<IPluginSource> sources, FileLog fileLog, ILog log, bool safeMode = false)
    {
        _safeMode = safeMode;
        _pluginsRoot = Path.Combine(environment.DataDirectory, "plugins");
        Ui = ui;
        Environment = environment;
        FileLog = fileLog;
        _sources = sources;
        _log = log;
        var configDirectory = Path.Combine(environment.DataDirectory, "config");
        _state = new KernelStateStore(Path.Combine(configDirectory, "kernel.json"), log);
        _configs = new PluginConfigStore(configDirectory, fileLog);
        Bus = new EventBus(ui, log);
    }

    public IUiDispatcher Ui { get; }

    public AppEnvironment Environment { get; }

    public FileLog FileLog { get; }

    public ServiceRegistry Registry { get; } = new();

    public EventBus Bus { get; }

    public IReadOnlyList<PluginInfo> Plugins
    {
        get
        {
            AssertUiThread();
            return [.. _entries.Select(ToInfo)];
        }
    }

    public static bool IsKernelService(Type type) => KernelServiceTypes.Contains(type);

    public void AssertUiThread()
    {
        if (!Ui.CheckAccess())
        {
            throw new InvalidOperationException("The plugin kernel must be used on the UI thread.");
        }
    }

    public void Start()
    {
        AssertUiThread();
        if (_started)
        {
            throw new InvalidOperationException("The kernel is already started.");
        }

        _started = true;
        Registry.Add(typeof(IPluginManager), this, null);
        Registry.Add(typeof(IAppEnvironment), Environment, null);
        Registry.Add(typeof(IUiDispatcher), Ui, null);

        _state.Load();
        if (_safeMode)
        {
            _log.Warn("Safe mode: only built-in plugins are loaded.");
        }

        PluginInstaller.ProcessQueues(_state, _pluginsRoot, _log);
        foreach (var candidate in Discover())
        {
            _entries.Add(CreateEntry(candidate));
        }

        _log.Info($"Discovered {_entries.Count} plugin(s).");
        Reconcile();
        Environment.IsStarted = true;
        foreach (var entry in _entries)
        {
            _log.Info($"Plugin '{entry.Id}' is {Describe(ToInfo(entry))}.");
        }

        Bus.Emit(new AppStarted());
    }

    public void SetEnabled(string pluginId, bool enabled)
    {
        AssertUiThread();
        var entry = _entries.FirstOrDefault(e => e.Id == pluginId)
            ?? throw new InvalidOperationException($"Unknown plugin '{pluginId}'.");
        if (entry.Manifest.Essential && !enabled)
        {
            throw new InvalidOperationException($"Plugin '{pluginId}' is essential and cannot be disabled.");
        }

        entry.Enabled = enabled;
        _state.SetEnabled(pluginId, enabled);

        if (entry.State == PluginState.PendingRemoval)
        {
            // The choice is stored and applies if the removal is undone.
            EmitStateChanged(entry);
            return;
        }

        if (!enabled)
        {
            if (entry.State == PluginState.Active)
            {
                DeactivateCore(entry, PluginState.Disabled);
            }
            else if (entry.State != PluginState.Disabled)
            {
                entry.State = PluginState.Disabled;
                EmitStateChanged(entry);
            }
        }
        else if (entry.State is PluginState.Disabled or PluginState.Failed)
        {
            entry.State = PluginState.Waiting;
            entry.Error = null;
            EmitStateChanged(entry);
        }

        Reconcile();
    }

    public bool RestartRequired => _state.PendingInstall.Count > 0;

    public async Task<PluginPackageInfo> InspectAsync(string zipOrFolderPath)
    {
        AssertUiThread();
        var (builtIn, _, versions) = SnapshotForPackages();
        var root = _pluginsRoot;
        try
        {
            var manifest = await Task.Run(() => PluginInstaller.Inspect(zipOrFolderPath, root, builtIn)).ConfigureAwait(false);
            return new PluginPackageInfo(true, manifest, versions.GetValueOrDefault(manifest.Id), null);
        }
        catch (PackageException ex)
        {
            return new PluginPackageInfo(false, null, null, ex.Message, ex.Code);
        }
        catch (Exception ex) when (IsPackageIoError(ex))
        {
            _log.Warn($"Could not inspect '{zipOrFolderPath}'.", ex);
            return new PluginPackageInfo(false, null, null, $"The package could not be read: {ex.Message}", PluginInstallError.IoError);
        }
    }

    public async Task<PluginInstallResult> InstallAsync(string zipOrFolderPath)
    {
        AssertUiThread();
        if (!_started || _shutDown)
        {
            throw new InvalidOperationException("The kernel is not running.");
        }

        if (Interlocked.CompareExchange(ref _installing, 1, 0) != 0)
        {
            return new PluginInstallResult(false, null, false, "Another plugin is being installed.", PluginInstallError.Busy);
        }

        try
        {
            var (builtIn, loaded, _) = SnapshotForPackages();
            var root = _pluginsRoot;
            var safeMode = _safeMode;
            PreparedInstall prepared;
            try
            {
                prepared = await Task.Run(() => PluginInstaller.Prepare(zipOrFolderPath, root, builtIn, loaded, safeMode)).ConfigureAwait(false);
            }
            catch (PackageException ex)
            {
                _log.Info($"Plugin package '{zipOrFolderPath}' was rejected: {ex.Message}");
                return new PluginInstallResult(false, ex.PluginId, false, ex.Message, ex.Code);
            }
            catch (Exception ex) when (IsPackageIoError(ex))
            {
                _log.Error($"Installing '{zipOrFolderPath}' failed.", ex);
                return new PluginInstallResult(false, null, false, $"The package could not be installed: {ex.Message}", PluginInstallError.IoError);
            }

            return await Ui.InvokeAsync(() => CompleteInstall(prepared)).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
        }
    }

    public void Uninstall(string pluginId)
    {
        AssertUiThread();
        var entry = _entries.FirstOrDefault(e => e.Id == pluginId)
            ?? throw new InvalidOperationException($"Unknown plugin '{pluginId}'.");
        if (entry.IsBuiltIn)
        {
            throw new InvalidOperationException($"Plugin '{pluginId}' is built in and cannot be removed.");
        }

        if (entry.State == PluginState.PendingRemoval)
        {
            return;
        }

        if (entry.State == PluginState.Active)
        {
            DeactivateCore(entry, PluginState.PendingRemoval);
        }
        else
        {
            entry.State = PluginState.PendingRemoval;
            entry.Error = null;
            EmitStateChanged(entry);
        }

        _state.AddPendingRemoval(pluginId);
        entry.Log.Info($"Plugin '{pluginId}' will be removed at the next start.");
        Reconcile();
    }

    public void UndoUninstall(string pluginId)
    {
        AssertUiThread();
        var entry = _entries.FirstOrDefault(e => e.Id == pluginId)
            ?? throw new InvalidOperationException($"Unknown plugin '{pluginId}'.");
        UndoRemoval(entry);
        Reconcile();
    }

    private PluginEntry CreateEntry(PluginCandidate candidate)
    {
        var id = candidate.Manifest.Id;
        var enabled = candidate.Manifest.Essential || (_state.GetEnabled(id) ?? candidate.Manifest.EnabledByDefault);
        return new PluginEntry(candidate, enabled, new PluginLog(FileLog, id), _configs.Get(id))
        {
            // A removal that failed at start-up (files in use) is retried at the next start.
            State = !candidate.IsBuiltIn && _state.IsPendingRemoval(id)
                ? PluginState.PendingRemoval
                : enabled ? PluginState.Waiting : PluginState.Disabled,
        };
    }

    private void UndoRemoval(PluginEntry entry)
    {
        if (entry.State != PluginState.PendingRemoval)
        {
            return;
        }

        _state.RemovePendingRemoval(entry.Id);
        entry.Error = null;
        entry.State = entry.Enabled ? PluginState.Waiting : PluginState.Disabled;
        entry.Log.Info($"Removal of plugin '{entry.Id}' was undone.");
        EmitStateChanged(entry);
    }

    private PluginInstallResult CompleteInstall(PreparedInstall prepared)
    {
        var manifest = prepared.Manifest;
        var id = manifest.Id;
        var existing = _entries.FirstOrDefault(e => e.Id == id);
        if (prepared.Staged)
        {
            _state.AddPendingInstall(id);
            if (existing is not null)
            {
                // Installing over a plugin that was just removed cancels the removal; the update replaces its files.
                UndoRemoval(existing);
                Reconcile();
            }

            _log.Info($"Plugin '{id}' {manifest.Version} is staged; it takes effect at the next start.");
            return new PluginInstallResult(true, id, true, null);
        }

        if (existing is not null)
        {
            return new PluginInstallResult(false, id, false, $"Plugin '{id}' is already installed.", PluginInstallError.IoError);
        }

        var entry = CreateEntry(DirectoryPluginSource.CreateCandidate(manifest, prepared.Folder, isBuiltIn: false));
        _entries.Add(entry);
        _log.Info($"Plugin '{id}' {manifest.Version} installed.");
        EmitStateChanged(entry);
        Reconcile();
        return new PluginInstallResult(true, id, false, null);
    }

    private (HashSet<string> BuiltIn, HashSet<string> Loaded, Dictionary<string, string> Versions) SnapshotForPackages() =>
    (
        [.. _entries.Where(e => e.IsBuiltIn).Select(e => e.Id)],
        [.. _entries.Select(e => e.Id)],
        _entries.ToDictionary(e => e.Id, e => e.Manifest.Version, StringComparer.Ordinal)
    );

    private static bool IsPackageIoError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException;

    public void Shutdown()
    {
        AssertUiThread();
        if (!_started || _shutDown)
        {
            return;
        }

        Bus.Emit(new AppShuttingDown());
        _shutDown = true;
        foreach (var entry in _entries.Where(e => e.State == PluginState.Active).OrderByDescending(e => e.ActivationOrder).ToList())
        {
            if (entry.State == PluginState.Active)
            {
                DeactivateCore(entry, PluginState.Waiting);
            }
        }

        _configs.FlushAll();
        _log.Info("Kernel shut down.");
    }

    public void Reconcile()
    {
        AssertUiThread();
        if (!_started || _shutDown)
        {
            return;
        }

        if (_reconciling)
        {
            _dirty = true;
            return;
        }

        _reconciling = true;
        try
        {
            var passes = 0;
            bool changed;
            do
            {
                if (++passes > MaxPasses)
                {
                    _log.Error($"Reconcile did not settle after {MaxPasses} passes; stopping.");
                    break;
                }

                _dirty = false;
                changed = false;

                // (a) requirements lost (safety net; the cascade normally handles this)
                foreach (var entry in ActiveNewestFirst())
                {
                    if (entry.State == PluginState.Active && entry.Requires.Any(t => !Registry.Has(t)))
                    {
                        DeactivateCore(entry, PluginState.Waiting);
                        changed = true;
                    }
                }

                // (b) optional availability changed since activation
                foreach (var entry in ActiveNewestFirst())
                {
                    if (entry.State == PluginState.Active && !entry.OptionalSnapshot.SequenceEqual(OptionalSnapshot(entry)))
                    {
                        DeactivateCore(entry, PluginState.Waiting);
                        changed = true;
                    }
                }

                // (c) activate
                foreach (var entry in _entries.Where(e => e.State == PluginState.Waiting).OrderBy(e => e.Id, StringComparer.Ordinal).ToList())
                {
                    if (entry.State != PluginState.Waiting || !EnsureLoaded(entry))
                    {
                        continue;
                    }

                    if (entry.Requires.All(Registry.Has))
                    {
                        ActivateCore(entry);
                        changed = true;
                    }
                }
            }
            while (changed || _dirty);
        }
        finally
        {
            _reconciling = false;
        }
    }

    private List<PluginCandidate> Discover()
    {
        var byId = new Dictionary<string, PluginCandidate>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var source in _sources)
        {
            IEnumerable<PluginCandidate> candidates;
            try
            {
                candidates = source.Discover(_log).ToList();
            }
            catch (Exception ex)
            {
                _log.Error("A plugin source failed.", ex);
                continue;
            }

            foreach (var candidate in candidates)
            {
                var id = candidate.Manifest.Id;
                if (byId.TryGetValue(id, out var existing))
                {
                    if (!existing.IsBuiltIn && candidate.IsBuiltIn)
                    {
                        byId[id] = candidate;
                        _log.Warn($"Duplicate plugin id '{id}': using the built-in copy in '{candidate.Directory}', ignoring '{existing.Directory}'.");
                    }
                    else
                    {
                        _log.Warn($"Duplicate plugin id '{id}': ignoring '{candidate.Directory}'.");
                    }

                    continue;
                }

                byId[id] = candidate;
                order.Add(id);
            }
        }

        return [.. order.Select(id => byId[id])];
    }

    private bool EnsureLoaded(PluginEntry entry)
    {
        if (entry.IsLoaded)
        {
            return true;
        }

        try
        {
            if (entry.Manifest.ApiVersion != Kernel.ApiVersion)
            {
                throw new InvalidOperationException(
                    $"Plugin API version {entry.Manifest.ApiVersion} is not supported (host: {Kernel.ApiVersion})");
            }

            var type = entry.Candidate.LoadEntryType();
            if (!typeof(Plugin).IsAssignableFrom(type) || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidOperationException(
                    $"Entry type {type.FullName} must be a non-abstract subclass of Plugin with a public parameterless constructor.");
            }

            var requires = type.GetCustomAttributes<RequiresAttribute>(inherit: false).SelectMany(a => a.Services).Distinct().ToArray();
            entry.Requires = requires;
            entry.Optional = [.. type.GetCustomAttributes<OptionalAttribute>(inherit: false).SelectMany(a => a.Services).Distinct().Except(requires)];
            entry.EntryType = type;
            return true;
        }
        catch (Exception ex)
        {
            var inner = Unwrap(ex);
            entry.State = PluginState.Failed;
            entry.Error = OneLine(inner.Message);
            entry.Log.Error($"Plugin '{entry.Id}' could not be loaded.", inner);
            EmitStateChanged(entry);
            return false;
        }
    }

    private void ActivateCore(PluginEntry entry)
    {
        Bus.Emit(new PluginActivating(entry.Manifest, entry.Directory));
        var context = new PluginContext(this, entry);
        entry.Context = context;
        entry.OptionalSnapshot = OptionalSnapshot(entry);
        try
        {
            entry.Instance = (Plugin)Activator.CreateInstance(entry.EntryType!)!;
            entry.Instance.Activate(context);
        }
        catch (Exception ex)
        {
            var inner = Unwrap(ex);
            Rollback(entry);
            entry.State = PluginState.Failed;
            entry.Error = OneLine(inner.Message);
            entry.Log.Error($"Plugin '{entry.Id}' failed to activate.", inner);
            EmitStateChanged(entry);
            return;
        }

        entry.State = PluginState.Active;
        entry.ActivationOrder = ++_activationCounter;
        entry.Log.Info($"Plugin '{entry.Id}' activated.");
        EmitStateChanged(entry);
    }

    private void DeactivateCore(PluginEntry entry, PluginState newState)
    {
        if (entry.State != PluginState.Active || !_deactivating.Add(entry))
        {
            return;
        }

        try
        {
            // Dependents first, so their undo actions run while this plugin's services are still alive.
            foreach (var dependent in _entries.Where(d => d != entry && d.State == PluginState.Active
                         && (d.Requires.Any(entry.Provided.Contains) || d.Optional.Any(entry.Provided.Contains))).ToList())
            {
                DeactivateCore(dependent, PluginState.Waiting);
            }

            entry.Context?.CancelDeactivating();
            try
            {
                entry.Instance?.Deactivate();
            }
            catch (Exception ex)
            {
                entry.Log.Error($"Plugin '{entry.Id}' threw in Deactivate.", ex);
            }

            Rollback(entry);
            entry.State = newState;
            entry.Log.Info($"Plugin '{entry.Id}' deactivated ({newState}).");
        }
        finally
        {
            _deactivating.Remove(entry);
        }

        EmitStateChanged(entry);
    }

    private void Rollback(PluginEntry entry)
    {
        var context = entry.Context;
        context?.RunEffects();
        foreach (var type in entry.Provided)
        {
            Registry.Remove(type, entry);
        }

        entry.Provided.Clear();
        context?.UnsubscribeAll();
        entry.Config.Flush();
        entry.Instance = null;
        entry.Context = null;
    }

    private IEnumerable<PluginEntry> ActiveNewestFirst() =>
        _entries.Where(e => e.State == PluginState.Active).OrderByDescending(e => e.ActivationOrder).ToList();

    private bool[] OptionalSnapshot(PluginEntry entry) => [.. entry.Optional.Select(Registry.Has)];

    private PluginInfo ToInfo(PluginEntry entry) => new(
        entry.Manifest,
        entry.State,
        entry.Enabled,
        entry.IsBuiltIn,
        entry.State == PluginState.Failed ? entry.Error : null,
        entry.State == PluginState.Waiting ? [.. entry.Requires.Where(t => !Registry.Has(t)).Select(t => t.Name)] : []);

    private void EmitStateChanged(PluginEntry entry) => Bus.Emit(new PluginStateChanged(ToInfo(entry)));

    private static string Describe(PluginInfo info) => info.State switch
    {
        PluginState.Failed => $"Failed: {info.Error}",
        PluginState.Waiting => $"Waiting for: {string.Join(", ", info.MissingServices)}",
        _ => info.State.ToString(),
    };

    private static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    private static string OneLine(string message) =>
        string.Join(' ', message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
