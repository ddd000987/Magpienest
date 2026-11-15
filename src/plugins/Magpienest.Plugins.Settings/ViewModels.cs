using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Magpienest.Sdk;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Settings;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Settings;

/// <summary>The settings page: section list on the left, the selected section on the right.</summary>
internal sealed partial class SettingsPageViewModel(ILog log) : ObservableObject, INavigationAware
{
    private readonly Dictionary<SettingsSection, FrameworkElement> _views = [];

    public ObservableCollection<SectionItem> Sections { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedView))]
    public partial SectionItem? SelectedSection { get; set; }

    public FrameworkElement? SelectedView => SelectedSection is null ? null : ViewFor(SelectedSection.Section);

    public void Add(SettingsSection section)
    {
        var item = new SectionItem(section);
        var index = 0;
        while (index < Sections.Count && Compare(Sections[index].Section, section) <= 0)
        {
            index++;
        }

        Sections.Insert(index, item);
        SelectedSection ??= Sections[0];
    }

    public void Remove(SettingsSection section)
    {
        var item = Sections.FirstOrDefault(s => s.Section == section);
        if (item is null)
        {
            return;
        }

        Sections.Remove(item);
        _views.Remove(section);
        if (SelectedSection == item)
        {
            SelectedSection = Sections.FirstOrDefault();
        }
    }

    public void Select(string sectionId)
    {
        var item = Sections.FirstOrDefault(s => s.Section.Id == sectionId);
        if (item is not null)
        {
            SelectedSection = item;
        }
    }

    public void RefreshTexts()
    {
        foreach (var item in Sections)
        {
            item.RefreshTitle();
        }
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is string sectionId)
        {
            Select(sectionId);
        }
    }

    public void OnNavigatedFrom()
    {
    }

    private FrameworkElement? ViewFor(SettingsSection section)
    {
        if (_views.TryGetValue(section, out var view))
        {
            return view;
        }

        try
        {
            view = section.CreateView();
            _views[section] = view;
            return view;
        }
        catch (Exception ex)
        {
            log.Error($"Creating settings section '{section.Id}' failed.", ex);
            return null;
        }
    }

    private static int Compare(SettingsSection a, SettingsSection b)
    {
        var order = a.Order.CompareTo(b.Order);
        return order != 0 ? order : StringComparer.Ordinal.Compare(a.Id, b.Id);
    }
}

/// <summary>A section entry in the list.</summary>
internal sealed class SectionItem(SettingsSection section) : ObservableObject
{
    public SettingsSection Section { get; } = section;

    public string Glyph => Section.Glyph;

    public string Title => Loc.Get(Section.TitleKey);

    public void RefreshTitle() => OnPropertyChanged(nameof(Title));
}

/// <summary>General: language and theme.</summary>
internal sealed partial class GeneralSectionViewModel : ObservableObject
{
    private const string SystemSetting = "system";

    private readonly ILocalizationService _localization;
    private readonly IShellService _shell;
    private bool _updating;

    public GeneralSectionViewModel(ILocalizationService localization, IShellService shell)
    {
        _localization = localization;
        _shell = shell;
        Themes =
        [
            new ChoiceOption<AppTheme>(AppTheme.System, () => Loc.Get("Settings.Theme.System")),
            new ChoiceOption<AppTheme>(AppTheme.Light, () => Loc.Get("Settings.Theme.Light")),
            new ChoiceOption<AppTheme>(AppTheme.Dark, () => Loc.Get("Settings.Theme.Dark")),
        ];
        Refresh();
    }

    public ObservableCollection<ChoiceOption<string>> Languages { get; } = [];

    public IReadOnlyList<ChoiceOption<AppTheme>> Themes { get; }

    [ObservableProperty]
    public partial ChoiceOption<string>? SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial ChoiceOption<AppTheme>? SelectedTheme { get; set; }

    /// <summary>Re-reads the language list, texts and current choices without writing anything back.</summary>
    public void Refresh()
    {
        _updating = true;
        try
        {
            var wanted = new List<(string Code, Func<string> Display)> { (SystemSetting, () => Loc.Get("Settings.General.SystemDefault")) };
            foreach (var language in _localization.AvailableLanguages)
            {
                var name = language.NativeName;
                wanted.Add((language.Code, () => name));
            }

            if (Languages.Select(l => l.Value).SequenceEqual(wanted.Select(w => w.Code), StringComparer.OrdinalIgnoreCase))
            {
                foreach (var option in Languages)
                {
                    option.Refresh();
                }
            }
            else
            {
                Languages.Clear();
                foreach (var (code, display) in wanted)
                {
                    Languages.Add(new ChoiceOption<string>(code, display));
                }
            }

            SelectedLanguage = Languages.FirstOrDefault(l => string.Equals(l.Value, _localization.LanguageSetting, StringComparison.OrdinalIgnoreCase))
                ?? Languages[0];

            foreach (var theme in Themes)
            {
                theme.Refresh();
            }

            SelectedTheme = Themes.First(t => t.Value == _shell.Theme);
        }
        finally
        {
            _updating = false;
        }
    }

    partial void OnSelectedLanguageChanged(ChoiceOption<string>? value)
    {
        if (!_updating && value is not null && !string.Equals(value.Value, _localization.LanguageSetting, StringComparison.OrdinalIgnoreCase))
        {
            _localization.LanguageSetting = value.Value;
        }
    }

    partial void OnSelectedThemeChanged(ChoiceOption<AppTheme>? value)
    {
        if (!_updating && value is not null)
        {
            _shell.Theme = value.Value;
        }
    }
}

/// <summary>A combo-box entry whose text is re-read on language change.</summary>
internal sealed class ChoiceOption<T>(T value, Func<string> display) : ObservableObject
{
    public T Value { get; } = value;

    public string Display => display();

    public void Refresh() => OnPropertyChanged(nameof(Display));
}

/// <summary>Plugins: one card per plugin.</summary>
internal sealed class PluginsSectionViewModel
{
    private readonly IPluginContext _ctx;
    private readonly IShellService _shell;
    private readonly ILocalizationService _localization;
    private readonly IPluginManager _manager;
    private readonly Dictionary<string, PluginCardViewModel> _cards = new(StringComparer.Ordinal);
    private bool _refreshQueued;

    public PluginsSectionViewModel(IPluginContext ctx, IShellService shell, ILocalizationService localization)
    {
        _ctx = ctx;
        _shell = shell;
        _localization = localization;
        _manager = ctx.Get<IPluginManager>();
        Refresh();
    }

    public ObservableCollection<PluginCardViewModel> Cards { get; } = [];

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
                card = new PluginCardViewModel(info.Manifest.Id, Toggle);
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
}

/// <summary>One plugin card.</summary>
internal sealed class PluginCardViewModel(string id, Action<string, bool> toggle) : ObservableObject
{
    private bool _isEnabled;

    public string Id { get; } = id;

    public string Name { get; private set; } = id;

    public string Description { get; private set; } = string.Empty;

    public string VersionText { get; private set; } = string.Empty;

    public string AuthorText { get; private set; } = string.Empty;

    public bool HasAuthor => AuthorText.Length > 0;

    public string StateText { get; private set; } = string.Empty;

    public PluginState State { get; private set; }

    public bool CanToggle { get; private set; }

    public string ToggleToolTip { get; private set; } = string.Empty;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (value != _isEnabled)
            {
                _isEnabled = value;
                OnPropertyChanged();
                toggle(Id, value);
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
        CanToggle = !manifest.Essential;
        ToggleToolTip = manifest.Essential ? Loc.Get("Settings.Plugins.Essential") : Loc.Get("Settings.Plugins.Toggle");
        _isEnabled = manifest.Essential || info.IsEnabled;
        OnPropertyChanged(string.Empty);
    }

    private static string? Pick(IReadOnlyDictionary<string, string> texts, string language)
    {
        if (texts.TryGetValue(language, out var text) || texts.TryGetValue("en", out text))
        {
            return text;
        }

        return texts.Values.FirstOrDefault();
    }
}
