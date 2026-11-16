using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;

namespace Magpienest.Plugins.Game;

/// <summary>The fields of the Add / Edit game dialog.</summary>
internal sealed partial class GameEditorViewModel : ObservableObject
{
    private readonly ILog _log;
    private readonly Func<string?> _pickExecutable;
    private readonly Func<string?> _pickFolder;
    private string _autoTitle = "";

    public GameEditorViewModel(ILog log, Func<string?>? pickExecutable = null, Func<string?>? pickFolder = null)
    {
        _log = log;
        _pickExecutable = pickExecutable ?? DefaultPickExecutable;
        _pickFolder = pickFolder ?? DefaultPickFolder;
    }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Executable { get; set; } = "";

    [ObservableProperty]
    public partial string Arguments { get; set; } = "";

    [ObservableProperty]
    public partial string WorkingFolder { get; set; } = "";

    [ObservableProperty]
    public partial string InstallFolder { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; set; }

    /// <summary>Whether <see cref="ErrorText"/> is shown.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>Whether the dialog may be accepted.</summary>
    public bool CanAccept => !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Executable);

    /// <summary>Fills the fields from an existing game.</summary>
    public static GameEditorViewModel For(LibraryItem item, ILog log, Func<string?>? pickExecutable = null, Func<string?>? pickFolder = null)
    {
        var extra = item.GetExtra<GameExtra>();
        return new GameEditorViewModel(log, pickExecutable, pickFolder)
        {
            Title = item.Title,
            Executable = item.Path,
            Arguments = extra.Arguments,
            WorkingFolder = extra.WorkingDir ?? "",
            InstallFolder = extra.InstallDir ?? "",
        };
    }

    /// <summary>The kind-specific JSON for the item.</summary>
    public string ToExtra() => LibraryItemExtensions.SerializeExtra(new GameExtra
    {
        InstallDir = GameExtra.NullIfBlank(InstallFolder),
        Arguments = Arguments.Trim(),
        WorkingDir = GameExtra.NullIfBlank(WorkingFolder),
    });

    /// <summary>Sets the program (as the browse button does): a shortcut is resolved and the title suggested.</summary>
    /// <param name="path">The chosen file.</param>
    public async Task ChooseExecutableAsync(string path)
    {
        ErrorText = null;
        var (resolved, arguments, folder, title) = await Task.Run<(string? Target, string? Arguments, string? Folder, string? Title)>(() =>
        {
            var target = path;
            string? args = null;
            string? workDir = null;
            if (ShortcutResolver.IsShortcut(path))
            {
                if (ShortcutResolver.Resolve(path) is not { } shortcut || ShortcutResolver.IsShortcut(shortcut.TargetPath))
                {
                    return (null, null, null, null);
                }

                target = shortcut.TargetPath;
                args = shortcut.Arguments;
                workDir = shortcut.WorkingDirectory;
            }

            return (target, args, workDir, GameFormat.DefaultTitle(target));
        });

        if (resolved is null)
        {
            ErrorText = Loc.Get("Game.Editor.ShortcutInvalid");
            return;
        }

        Executable = resolved;
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            Arguments = arguments;
        }

        if (!string.IsNullOrWhiteSpace(folder))
        {
            WorkingFolder = folder;
        }

        if (string.IsNullOrWhiteSpace(Title) || Title == _autoTitle)
        {
            Title = title ?? "";
            _autoTitle = Title;
        }
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(CanAccept));

    partial void OnExecutableChanged(string value)
    {
        OnPropertyChanged(nameof(CanAccept));
        // A shortcut typed or pasted into the box is read like a chosen one.
        if (ShortcutResolver.IsShortcut(value.Trim()))
        {
            _ = ResolveTypedShortcutAsync(value.Trim());
        }
    }

    private async Task ResolveTypedShortcutAsync(string path)
    {
        try
        {
            await ChooseExecutableAsync(path);
        }
        catch (Exception ex)
        {
            _log.Warn($"Reading the shortcut {path} failed.", ex);
        }
    }

    [RelayCommand]
    private async Task BrowseExecutable()
    {
        if (_pickExecutable() is { } path)
        {
            await ChooseExecutableAsync(path);
        }
    }

    [RelayCommand]
    private void BrowseWorkingFolder()
    {
        if (_pickFolder() is { } folder)
        {
            WorkingFolder = folder;
        }
    }

    [RelayCommand]
    private void BrowseInstallFolder()
    {
        if (_pickFolder() is { } folder)
        {
            InstallFolder = folder;
        }
    }

    private static string? DefaultPickExecutable()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("Game.Editor.ChooseExecutable"),
            Filter = Loc.Get("Game.Editor.ExecutableFilter") + "|*.exe;*.bat;*.cmd;*.lnk",
            CheckFileExists = true,
            DereferenceLinks = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? DefaultPickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
