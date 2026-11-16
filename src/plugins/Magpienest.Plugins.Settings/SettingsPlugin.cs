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
        var service = new SettingsService(ctx, shell, page);
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
internal sealed class SettingsService : ISettingsService
{
    private readonly IShellService _shell;
    private readonly SettingsPageViewModel _page;

    /// <param name="ctx">The Settings plugin's context; owns the dot on the Settings navigation entry.</param>
    /// <param name="shell">The shell.</param>
    /// <param name="page">The Settings page.</param>
    public SettingsService(IPluginContext ctx, IShellService shell, SettingsPageViewModel page)
    {
        _shell = shell;
        _page = page;
        page.Badges.Changed += id =>
        {
            page.RefreshBadge(id);
            shell.SetBadge(ctx, SettingsPlugin.PageId, page.Badges.Any);
        };
    }

    public void SetBadge(IPluginContext owner, string sectionId, bool visible)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrEmpty(sectionId);
        _page.Badges.Set(owner, sectionId, visible);
    }

    public void AddSection(IPluginContext owner, SettingsSection section)
    {
        _page.Add(section);
        owner.Track(() => _page.Remove(section));
    }

    public void Open(string? sectionId = null)
    {
        if (sectionId is not null)
        {
            _page.Select(sectionId);
        }

        _shell.Navigate(SettingsPlugin.PageId, sectionId);
    }
}
