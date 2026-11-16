using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Settings;

/// <summary>Asks the user for a plugin package.</summary>
internal interface IPackagePicker
{
    /// <summary>Lets the user choose a .zip file; null when cancelled.</summary>
    string? PickZip();

    /// <summary>Lets the user choose a folder; null when cancelled.</summary>
    string? PickFolder();
}

/// <summary>The Windows file and folder dialogs.</summary>
internal sealed class WindowsPackagePicker(IShellService shell) : IPackagePicker
{
    public string? PickZip()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("Settings.Plugins.Pick.ZipTitle"),
            Filter = Loc.Get("Settings.Plugins.Pick.ZipFilter") + "|*.zip",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(shell.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.Get("Settings.Plugins.Pick.FolderTitle"),
            Multiselect = false,
        };
        return dialog.ShowDialog(shell.MainWindow) == true ? dialog.FolderName : null;
    }
}

/// <summary>Plugins: install buttons, a restart bar and one card per plugin.</summary>
internal sealed partial class PluginsSectionViewModel : ObservableObject
{
    private readonly IPluginContext _ctx;
    private readonly IShellService _shell;
    private readonly ILocalizationService _localization;
    private readonly IPluginManager _manager;
    private readonly IPackagePicker _picker;
    private readonly Dictionary<string, PluginCardViewModel> _cards = new(StringComparer.Ordinal);
    private bool _refreshQueued;
    private bool _installNeedsRestart;

    public PluginsSectionViewModel(IPluginContext ctx, IShellService shell, ILocalizationService localization)
    {
        _ctx = ctx;
        _shell = shell;
        _localization = localization;
        _manager = ctx.Get<IPluginManager>();
        _picker = new WindowsPackagePicker(shell);
        Refresh();
    }

    public ObservableCollection<PluginCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    public partial bool ShowRestartBar { get; set; }

    [ObservableProperty]
    public partial string RestartBarText { get; set; } = string.Empty;

    /// <summary>Coalesces refreshes and runs them after the current kernel operation.</summary>
    public void ScheduleRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        _ctx.Ui.Post(() =>
        {
            _refreshQueued = false;
            if (!_ctx.Deactivating.IsCancellationRequested)
            {
                Refresh();
            }
        });
    }

    public void Refresh()
    {
        var infos = _manager.Plugins;
        var language = _localization.CurrentLanguage;
        foreach (var info in infos)
        {
            if (!_cards.TryGetValue(info.Manifest.Id, out var card))
            {
                card = new PluginCardViewModel(info.Manifest.Id, Toggle, RemoveAsync, UndoAsync);
                _cards[info.Manifest.Id] = card;
            }

            card.Update(info, language);
        }

        foreach (var id in _cards.Keys.Except(infos.Select(i => i.Manifest.Id)).ToList())
        {
            _cards.Remove(id);
        }

        var sorted = _cards.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
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

        UpdateRestartBar(infos);
    }

    private void UpdateRestartBar(IReadOnlyList<PluginInfo> infos)
    {
        var key = PluginInstallMessages.RestartBarKey(
            _manager.RestartRequired || _installNeedsRestart,
            infos.Any(i => i.State == PluginState.PendingRemoval));
        ShowRestartBar = key is not null;
        RestartBarText = key is null ? string.Empty : Loc.Get(key);
    }

    private void Toggle(string id, bool enabled)
    {
        try
        {
            _manager.SetEnabled(id, enabled);
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn($"SetEnabled('{id}', {enabled}) failed.", ex);
            _shell.Notify(Loc.Format("Settings.Plugins.ToggleFailed", ex.Message), NotifySeverity.Error);
        }

        ScheduleRefresh();
    }

    [RelayCommand]
    private Task InstallZip() => InstallFromAsync(_picker.PickZip());

    [RelayCommand]
    private Task InstallFolder() => InstallFromAsync(_picker.PickFolder());

    private async Task InstallFromAsync(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            var package = await _manager.InspectAsync(path);
            if (!package.IsValid || package.Manifest is null)
            {
                _ctx.Log.Info($"Plugin package '{path}' rejected: {package.Error}");
                await ShowInstallErrorAsync(package.ErrorCode);
                return;
            }

            var manifest = package.Manifest;
            var language = _localization.CurrentLanguage;
            var name = PluginCardViewModel.Pick(manifest.Name, language) ?? manifest.Id;
            var confirmed = await _shell.ConfirmAsync(
                Loc.Get("Settings.Plugins.Install.ConfirmTitle"),
                BuildConfirmMessage(name, manifest, package.InstalledVersion),
                Loc.Get("Settings.Plugins.Install.ConfirmButton"));
            if (!confirmed)
            {
                return;
            }

            var result = await _manager.InstallAsync(path);
            if (!result.Success)
            {
                _ctx.Log.Warn($"Installing '{path}' failed: {result.Error}");
                await ShowInstallErrorAsync(result.ErrorCode);
                return;
            }

            _installNeedsRestart |= result.RestartRequired;
            _ctx.Log.Info($"Installed plugin '{result.PluginId}' from '{path}'.");
            _shell.Notify(
                Loc.Format(result.RestartRequired ? "Settings.Plugins.Install.SuccessRestart" : "Settings.Plugins.Install.Success", name),
                NotifySeverity.Success);
        }
        catch (Exception ex)
        {
            _ctx.Log.Error($"Installing '{path}' failed unexpectedly.", ex);
            await ShowInstallErrorAsync(PluginInstallError.None);
        }
        finally
        {
            ScheduleRefresh();
        }
    }

    private static string BuildConfirmMessage(string name, PluginManifest manifest, string? installedVersion)
    {
        var author = string.IsNullOrWhiteSpace(manifest.Author) ? Loc.Get("Settings.Plugins.Install.AuthorUnknown") : manifest.Author;
        var lines = new List<string> { Loc.Format("Settings.Plugins.Install.Details", name, manifest.Version, author) };
        if (installedVersion is not null)
        {
            lines.Add(Loc.Format("Settings.Plugins.Install.UpdateNote", installedVersion, manifest.Version));
        }

        lines.Add(Loc.Get("Settings.Plugins.Install.Warning"));
        return string.Join("\n\n", lines);
    }

    private Task ShowInstallErrorAsync(PluginInstallError code) =>
        _shell.ShowMessageAsync(Loc.Get("Settings.Plugins.Install.FailedTitle"), Loc.Get(PluginInstallMessages.KeyFor(code)));

    private async Task RemoveAsync(string id)
    {
        var name = _cards.TryGetValue(id, out var card) ? card.Name : id;
        var confirmed = await _shell.ConfirmAsync(
            Loc.Get("Settings.Plugins.Remove.ConfirmTitle"),
            Loc.Format("Settings.Plugins.Remove.ConfirmMessage", name),
            Loc.Get("Settings.Plugins.Remove"));
        if (confirmed)
        {
            Change(() => _manager.Uninstall(id), $"Uninstall('{id}')");
        }
    }

    private Task UndoAsync(string id)
    {
        Change(() => _manager.UndoUninstall(id), $"UndoUninstall('{id}')");
        return Task.CompletedTask;
    }

    private void Change(Action action, string description)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn($"{description} failed.", ex);
            _shell.Notify(Loc.Format("Settings.Plugins.ToggleFailed", ex.Message), NotifySeverity.Error);
        }

        Refresh();
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
            _shell.Notify(Loc.Format("Settings.Plugins.RestartFailed", ex.Message), NotifySeverity.Error);
            return;
        }

        _ctx.Environment.RequestShutdown();
    }
}

/// <summary>One plugin card.</summary>
internal sealed class PluginCardViewModel : ObservableObject
{
    private readonly Action<string, bool> _toggle;
    private bool _isEnabled;

    public PluginCardViewModel(string id, Action<string, bool> toggle, Func<string, Task>? remove = null, Func<string, Task>? undo = null)
    {
        Id = id;
        Name = id;
        _toggle = toggle;
        RemoveCommand = new AsyncRelayCommand(() => remove?.Invoke(Id) ?? Task.CompletedTask);
        UndoCommand = new AsyncRelayCommand(() => undo?.Invoke(Id) ?? Task.CompletedTask);
    }

    public string Id { get; }

    public string Name { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public string VersionText { get; private set; } = string.Empty;

    public string AuthorText { get; private set; } = string.Empty;

    public bool HasAuthor => AuthorText.Length > 0;

    public string StateText { get; private set; } = string.Empty;

    public PluginState State { get; private set; }

    public bool CanToggle { get; private set; }

    public string ToggleToolTip { get; private set; } = string.Empty;

    /// <summary>Shipped with the app: shows the "Built-in" label.</summary>
    public bool IsBuiltIn { get; private set; }

    /// <summary>User-installed and not yet queued for removal: shows "Remove".</summary>
    public bool CanRemove { get; private set; }

    /// <summary>Queued for removal: shows "Undo".</summary>
    public bool CanUndo { get; private set; }

    public ICommand RemoveCommand { get; }

    public ICommand UndoCommand { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (value != _isEnabled)
            {
                _isEnabled = value;
                OnPropertyChanged();
                _toggle(Id, value);
            }
        }
    }

    public void Update(PluginInfo info, string language)
    {
        var manifest = info.Manifest;
        Name = Pick(manifest.Name, language) ?? manifest.Id;
        Description = Pick(manifest.Description, language) ?? string.Empty;
        VersionText = Loc.Format("Settings.Plugins.Version", manifest.Version);
        AuthorText = string.IsNullOrWhiteSpace(manifest.Author) ? string.Empty : Loc.Format("Settings.Plugins.Author", manifest.Author);
        State = info.State;
        StateText = info.State switch
        {
            PluginState.Active => Loc.Get("Settings.Plugins.State.Active"),
            PluginState.Disabled => Loc.Get("Settings.Plugins.State.Disabled"),
            PluginState.Waiting => Loc.Format("Settings.Plugins.State.Waiting", string.Join(", ", info.MissingServices)),
            PluginState.Failed => Loc.Format("Settings.Plugins.State.Failed", info.Error),
            _ => Loc.Get("Settings.Plugins.State.PendingRemoval"),
        };
        IsBuiltIn = info.IsBuiltIn;
        CanRemove = !info.IsBuiltIn && info.State != PluginState.PendingRemoval;
        CanUndo = info.State == PluginState.PendingRemoval;
        CanToggle = !manifest.Essential && info.State != PluginState.PendingRemoval;
        ToggleToolTip = manifest.Essential ? Loc.Get("Settings.Plugins.Essential") : Loc.Get("Settings.Plugins.Toggle");
        _isEnabled = manifest.Essential || info.IsEnabled;
        OnPropertyChanged(string.Empty);
    }

    internal static string? Pick(IReadOnlyDictionary<string, string> texts, string language)
    {
        if (texts.TryGetValue(language, out var text) || texts.TryGetValue("en", out text))
        {
            return text;
        }

        return texts.Values.FirstOrDefault();
    }
}
