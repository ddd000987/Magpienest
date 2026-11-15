using Magpienest.Plugins.Library.Browser;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Settings;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Thumbnails;

namespace Magpienest.Plugins.Library;

/// <summary>Provides <see cref="ILibraryService"/>: the collection database, kinds, tags and scanning.</summary>
[Requires(typeof(IShellService), typeof(ISettingsService), typeof(IThumbnailService))]
public sealed class LibraryPlugin : Plugin
{
    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var shell = ctx.Get<IShellService>();
        var root = Path.Combine(ctx.Environment.DataDirectory, "library");

        var service = new LibraryService(
            Path.Combine(root, "library.db"),
            Path.Combine(root, "media"),
            evt => ctx.Emit(evt),
            message => shell.Notify(message, NotifySeverity.Warning));
        ctx.Track(service);
        ctx.Provide<ILibraryService>(service);

        var browsers = new LibraryBrowserFactory(new BrowserServices
        {
            Library = service,
            Thumbnails = ctx.Get<IThumbnailService>(),
            Shell = shell,
            Config = ctx.Config,
            Log = ctx.Log,
        });
        ctx.On<LibraryChanged>(browsers.OnLibraryChanged);
        ctx.Provide<ILibraryBrowserFactory>(browsers);

        var startup = new StartupScan(ctx, service, shell);
        ctx.On<AppStarted>(startup.OnAppStarted);
        ctx.Get<ISettingsService>().AddSection(ctx, new SettingsSection
        {
            Id = "library",
            TitleKey = "Library.Settings.Title",
            Glyph = "",
            Order = 100,
            CreateView = () => new LibrarySettingsView
            {
                DataContext = new LibrarySettingsViewModel(ctx, service, ctx.Get<IThumbnailService>(), shell),
            },
        });

        // Opening and migrating the database runs on a background thread; report a failure instead of blocking activation.
        _ = service.Ready.ContinueWith(
            task =>
            {
                ctx.Log.Error("The library database could not be opened.", task.Exception?.GetBaseException());
                shell.Notify(Loc.Get("Library.Error.DatabaseOpenFailed"), NotifySeverity.Error);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
