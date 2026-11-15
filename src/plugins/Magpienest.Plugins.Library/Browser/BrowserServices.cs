using CommunityToolkit.Mvvm.ComponentModel;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Thumbnails;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>Everything a browser needs from the outside; tests pass fakes.</summary>
internal sealed class BrowserServices
{
    public required ILibraryService Library { get; init; }

    public required IThumbnailService Thumbnails { get; init; }

    public required IShellService Shell { get; init; }

    /// <summary>The Library plugin's config (browser.&lt;kind&gt;.* keys).</summary>
    public required IPluginConfig Config { get; init; }

    public required ILog Log { get; init; }

    /// <summary>Debounce wait; tests replace it.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Folder picker; null result = cancelled. Tests replace it.</summary>
    public Func<string?> PickFolder { get; init; } = DefaultPickFolder;

    private static string? DefaultPickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

/// <summary>A choice in a combo box whose label is a localization key (re-read on language change).</summary>
/// <typeparam name="T">Value type.</typeparam>
internal sealed class LocalizedOption<T>(T value, string key, params object?[] args) : ObservableObject
{
    public T Value { get; } = value;

    public string Label => args.Length == 0 ? Loc.Get(key) : Loc.Format(key, args);

    public void Refresh() => OnPropertyChanged(nameof(Label));

    public override string ToString() => Label;
}
