using Magpienest.Sdk;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Settings;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Settings;

/// <summary>Provides <see cref="ISettingsService"/> and the Settings page with the General and Plugins sections.</summary>
[Requires(typeof(IShellService), typeof(ILocalizationService))]
public sealed class SettingsPlugin : Plugin
{
    internal const string PageId = "settings";

    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var shell = ctx.Get<IShellService>();
        var localization = ctx.Get<ILocalizationService>();

        var page = new SettingsPageViewModel(ctx.Log);
        var service = new SettingsService(shell, page);
        ctx.Provide<ISettingsService>(service);

        shell.AddPage(ctx, new PageDescriptor
        {
            Id = PageId,
            TitleKey = "Settings.Nav.Title",
            Glyph = "",
            Order = 1000,
            Placement = NavPlacement.Footer,
            CreateView = () => new SettingsPage { DataContext = page },
        });

        var general = new GeneralSectionViewModel(localization, shell);
        service.AddSection(ctx, new SettingsSection
        {
            Id = "general",
            TitleKey = "Settings.General.Title",
            Glyph = "",
            Order = 0,
            CreateView = () => new GeneralSectionView { DataContext = general },
        });

        var plugins = new PluginsSectionViewModel(ctx, shell, localization);
        service.AddSection(ctx, new SettingsSection
        {
            Id = "plugins",
            TitleKey = "Settings.Plugins.Title",
            Glyph = "",
            Order = 900,
            CreateView = () => new PluginsSectionView { DataContext = plugins },
        });

        ctx.On<LanguageChanged>(_ =>
        {
            page.RefreshTexts();
            general.Refresh();
            plugins.ScheduleRefresh();
        });
        ctx.On<ThemeChanged>(_ => general.Refresh());
        ctx.On<PluginStateChanged>(_ => plugins.ScheduleRefresh());
    }
}

/// <summary><see cref="ISettingsService"/> implementation.</summary>
internal sealed class SettingsService(IShellService shell, SettingsPageViewModel page) : ISettingsService
{
    public void AddSection(IPluginContext owner, SettingsSection section)
    {
        page.Add(section);
        owner.Track(() => page.Remove(section));
    }

    public void Open(string? sectionId = null)
    {
        if (sectionId is not null)
        {
            page.Select(sectionId);
        }

        shell.Navigate(SettingsPlugin.PageId, sectionId);
    }
}
