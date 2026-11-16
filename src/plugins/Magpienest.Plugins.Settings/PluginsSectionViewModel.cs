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

/// <summary>Plugins: one card per bundle (T8.0), a restart bar, and the details switch with the install buttons.</summary>
internal sealed partial class PluginsSectionViewModel : ObservableObject
{
    internal const string ShowDetailsKey = "showPluginDetails";

    private readonly IPluginContext _ctx;
    private readonly IShellService _shell;
    private readonly ILocalizationService _localization;
    private readonly IPluginManager _manager;
    private readonly IPackagePicker _picker;
    private readonly Dictionary<string, BundleCardViewModel> _cards = new(StringComparer.Ordinal);
    private bool _refreshQueued;
    private bool _installNeedsRestart;

    public PluginsSectionViewModel(IPluginContext ctx, IShellService shell, ILocalizationService localization)
    {
        _ctx = ctx;
        _shell = shell;
        _localization = localization;
        _manager = ctx.Get<IPluginManager>();
        _picker = new WindowsPackagePicker(shell);
        ShowDetails = ctx.Config.Get(ShowDetailsKey, false);
        Refresh();
    }

    public ObservableCollection<BundleCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    public partial bool ShowRestartBar { get; set; }

    [ObservableProperty]
    public partial string RestartBarText { get; set; } = string.Empty;

    /// <summary>Shows each card's plugins and the install-from-file buttons; remembered in the config.</summary>
    [ObservableProperty]
    public partial bool ShowDetails { get; set; }

    partial void OnShowDetailsChanged(bool value) => _ctx.Config.Set(ShowDetailsKey, value);

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
        var bundles = PluginBundles.Group(infos, b => BundleCardViewModel.NameOf(b, language));
        foreach (var bundle in bundles)
        {
            if (!_cards.TryGetValue(bundle.Key, out var card))
            {
                card = new BundleCardViewModel(bundle.Key, Toggle, RemoveAsync, UndoAsync);
                _cards[bundle.Key] = card;
            }

            card.Update(bundle, language);
        }

        foreach (var key in _cards.Keys.Except(bundles.Select(b => b.Key)).ToList())
        {
            _cards.Remove(key);
        }

        var sorted = bundles.Select(b => _cards[b.Key]).ToList();
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

    private void Toggle(BundleCardViewModel card, bool enabled)
    {
        foreach (var id in card.ToggleIds)
        {
            try
            {
                _manager.SetEnabled(id, enabled);
            }
            catch (Exception ex)
            {
                _ctx.Log.Warn($"SetEnabled('{id}', {enabled}) failed.", ex);
                _shell.Notify(Loc.Format("Settings.Plugins.ToggleFailed", card.Name), NotifySeverity.Error);
                break;
            }
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
            var name = BundleCardViewModel.Pick(manifest.Name, language) ?? manifest.Id;
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

    private async Task RemoveAsync(BundleCardViewModel card)
    {
        var confirmed = await _shell.ConfirmAsync(
            Loc.Get("Settings.Plugins.Remove.ConfirmTitle"),
            Loc.Format("Settings.Plugins.Remove.ConfirmMessage", card.Name),
            Loc.Get("Settings.Plugins.Remove"));
        if (confirmed)
        {
            Change(card, card.RemovableIds, _manager.Uninstall, "Uninstall");
        }
    }

    private Task UndoAsync(BundleCardViewModel card)
    {
        Change(card, card.PendingIds, _manager.UndoUninstall, "UndoUninstall");
        return Task.CompletedTask;
    }

    private void Change(BundleCardViewModel card, IReadOnlyList<string> ids, Action<string> action, string description)
    {
        foreach (var id in ids)
        {
            try
            {
                action(id);
            }
            catch (Exception ex)
            {
                _ctx.Log.Warn($"{description}('{id}') failed.", ex);
                _shell.Notify(Loc.Format("Settings.Plugins.ToggleFailed", card.Name), NotifySeverity.Error);
                break;
            }
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
