using Magpienest.Sdk;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Settings;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Catalogue;

/// <summary>
/// Plugin store: reads the official catalogue from the store repository and installs bundles from
/// its releases, on the "Plugin store" page in Settings.
/// </summary>
[Requires(typeof(IShellService), typeof(ISettingsService), typeof(ILocalizationService))]
public sealed class CataloguePlugin : Plugin
{
    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var shell = ctx.Get<IShellService>();
        var settings = ctx.Get<ISettingsService>();
        var manager = ctx.Get<IPluginManager>();
        var service = new StoreService(ctx, shell, settings, ctx.Get<ILocalizationService>(), manager);
        ctx.Track(service);

        var page = new StorePageViewModel(ctx, service, shell, manager);
        settings.AddSection(ctx, new SettingsSection
        {
            Id = StoreService.SectionId,
            TitleKey = "Catalogue.Page.Title",
            Glyph = "",
            Order = 910,
            CreateView = () => new StorePageView { DataContext = page },
        });

        ctx.On<AppStarted>(_ => service.OnAppStarted());
        ctx.On<PluginStateChanged>(_ => service.ScheduleRaise());
        ctx.On<LanguageChanged>(_ => service.ScheduleRaise());
    }
}
