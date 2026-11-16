using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk;
using Magpienest.Sdk.Localization;

namespace Magpienest.Plugins.Settings;

/// <summary>One plugin of a bundle card, shown under "Show details".</summary>
/// <param name="Name">The plugin's name.</param>
/// <param name="VersionText">"Version 0.1.0".</param>
/// <param name="StateText">The technical state (missing services, error text).</param>
/// <param name="State">The plugin's state, for the badge colour.</param>
internal sealed record BundleMemberRow(string Name, string VersionText, string StateText, PluginState State);

/// <summary>One bundle card: what <see cref="PluginBundle"/> decided, as texts and switches.</summary>
internal sealed class BundleCardViewModel : ObservableObject
{
    private readonly Action<BundleCardViewModel, bool> _toggle;
    private bool _isEnabled;

    public BundleCardViewModel(
        string key,
        Action<BundleCardViewModel, bool> toggle,
        Func<BundleCardViewModel, Task>? remove = null,
        Func<BundleCardViewModel, Task>? undo = null)
    {
        Key = key;
        Name = key;
        _toggle = toggle;
        RemoveCommand = new AsyncRelayCommand(() => remove?.Invoke(this) ?? Task.CompletedTask);
        UndoCommand = new AsyncRelayCommand(() => undo?.Invoke(this) ?? Task.CompletedTask);
    }

    public string Key { get; }

    public string Name { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public string VersionText { get; private set; } = string.Empty;

    public string AuthorText { get; private set; } = string.Empty;

    public bool HasAuthor => AuthorText.Length > 0;

    public string StateText { get; private set; } = string.Empty;

    public BundleState State { get; private set; }

    /// <summary>Shipped with the app: shows the "Built-in" label.</summary>
    public bool IsBuiltIn { get; private set; }

    /// <summary>Essential bundles have no switch.</summary>
    public bool ShowToggle { get; private set; }

    public bool CanToggle { get; private set; }

    public bool CanRemove { get; private set; }

    public bool CanUndo { get; private set; }

    public IReadOnlyList<BundleMemberRow> Members { get; private set; } = [];

    public IReadOnlyList<string> ToggleIds { get; private set; } = [];

    public IReadOnlyList<string> RemovableIds { get; private set; } = [];

    public IReadOnlyList<string> PendingIds { get; private set; } = [];

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
                _toggle(this, value);
            }
        }
    }

    public void Update(PluginBundle bundle, string language)
    {
        var lead = bundle.Lead.Manifest;
        Name = NameOf(bundle, language);
        Description = bundle.BundleId is { } id && Known(PluginBundles.DescriptionKey(id)) is { } description
            ? description
            : Pick(lead.Description, language) ?? string.Empty;
        VersionText = Loc.Format("Settings.Plugins.Version", bundle.Version);
        var authors = bundle.Members.Select(m => m.Manifest.Author).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct().ToList();
        AuthorText = authors.Count == 0 ? string.Empty : Loc.Format("Settings.Plugins.Author", string.Join(", ", authors));
        State = bundle.State;
        StateText = Loc.Get(bundle.State switch
        {
            BundleState.On => "Settings.Plugins.State.Active",
            BundleState.PartlyOn => "Settings.Plugins.State.PartlyOn",
            BundleState.Off => "Settings.Plugins.State.Disabled",
            BundleState.Waiting => "Settings.Plugins.State.WaitingBundle",
            BundleState.Failed => "Settings.Plugins.State.FailedBundle",
            _ => "Settings.Plugins.State.PendingRemoval",
        });
        IsBuiltIn = bundle.IsBuiltIn;
        ShowToggle = !bundle.IsEssential;
        CanToggle = bundle.CanToggle;
        CanRemove = bundle.CanRemove;
        CanUndo = bundle.CanUndo;
        ToggleIds = bundle.ToggleIds.ToList();
        RemovableIds = bundle.RemovableIds.ToList();
        PendingIds = bundle.PendingIds.ToList();
        Members = bundle.Members
            .Select(m => new BundleMemberRow(
                Pick(m.Manifest.Name, language) ?? m.Manifest.Id,
                Loc.Format("Settings.Plugins.Version", m.Manifest.Version),
                MemberStateText(m),
                m.State))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _isEnabled = bundle.IsEnabled;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>The card's name: the bundle's from the language files, else its lead plugin's.</summary>
    internal static string NameOf(PluginBundle bundle, string language) =>
        bundle.BundleId is { } id && Known(PluginBundles.NameKey(id)) is { } name
            ? name
            : Pick(bundle.Lead.Manifest.Name, language) ?? bundle.Lead.Manifest.Id;

    internal static string? Pick(IReadOnlyDictionary<string, string> texts, string language)
    {
        if (texts.TryGetValue(language, out var text) || texts.TryGetValue("en", out text))
        {
            return text;
        }

        return texts.Values.FirstOrDefault();
    }

    private static string MemberStateText(PluginInfo info) => info.State switch
    {
        PluginState.Active => Loc.Get("Settings.Plugins.State.Active"),
        PluginState.Disabled => Loc.Get("Settings.Plugins.State.Disabled"),
        PluginState.Waiting => Loc.Format("Settings.Plugins.State.Waiting", string.Join(", ", info.MissingServices)),
        PluginState.Failed => Loc.Format("Settings.Plugins.State.Failed", info.Error),
        _ => Loc.Get("Settings.Plugins.State.PendingRemoval"),
    };

    /// <summary>The text of a key, or null when no language file has it (Loc returns the key itself then).</summary>
    private static string? Known(string key)
    {
        var text = Loc.Get(key);
        return text == key ? null : text;
    }
}
