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

    /// <summary>Dots on sections (<see cref="ISettingsService.SetBadge"/>).</summary>
    public BadgeSet Badges { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedView))]
    public partial SectionItem? SelectedSection { get; set; }

    public FrameworkElement? SelectedView => SelectedSection is null ? null : ViewFor(SelectedSection.Section);

    public void Add(SettingsSection section)
    {
        var item = new SectionItem(section) { HasBadge = Badges.Has(section.Id) };
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

    /// <summary>Updates the dot of a section after <see cref="Badges"/> changed.</summary>
    public void RefreshBadge(string sectionId)
    {
        foreach (var item in Sections.Where(s => s.Section.Id == sectionId))
        {
            item.HasBadge = Badges.Has(sectionId);
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
internal sealed partial class SectionItem(SettingsSection section) : ObservableObject
{
    public SettingsSection Section { get; } = section;

    [ObservableProperty]
    public partial bool HasBadge { get; set; }

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
