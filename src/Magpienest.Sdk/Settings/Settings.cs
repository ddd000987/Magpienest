using System.Windows;

namespace Magpienest.Sdk.Settings;

/// <summary>Provided by the Settings plugin.</summary>
public interface ISettingsService
{
    /// <summary>Adds a section; removed when <paramref name="owner"/> deactivates.</summary>
    /// <param name="owner">Registering plugin.</param>
    /// <param name="section">The section.</param>
    void AddSection(IPluginContext owner, SettingsSection section);

    /// <summary>Navigates to the settings page and selects the section.</summary>
    /// <param name="sectionId">Section id, or null for the current one.</param>
    void Open(string? sectionId = null);
}

/// <summary>A settings section.</summary>
public sealed record SettingsSection
{
    /// <summary>Section id, e.g. "general".</summary>
    public required string Id { get; init; }

    /// <summary>Localization key of the title.</summary>
    public required string TitleKey { get; init; }

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; init; } = "";

    /// <summary>Ascending sort order.</summary>
    public int Order { get; init; }

    /// <summary>Creates the section view.</summary>
    public required Func<FrameworkElement> CreateView { get; init; }
}
