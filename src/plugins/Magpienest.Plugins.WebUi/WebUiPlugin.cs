using System.Text.Json;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Settings;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Thumbnails;

namespace Magpienest.Plugins.WebUi;

/// <summary>
/// The web interface: serves the library to a browser. Installed separately; the desktop interface never
/// depends on it.
/// </summary>
[Requires(typeof(IShellService), typeof(ISettingsService), typeof(ILibraryService), typeof(IThumbnailService),
          typeof(IImagingService))]
[Optional(typeof(ILocalizationService))]
public sealed class WebUiPlugin : Plugin
{
    /// <summary>Settings section id.</summary>
    public const string SectionId = "webui";

    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var shell = ctx.Get<IShellService>();
        var library = ctx.Get<ILibraryService>();
        ctx.TryGet<ILocalizationService>(out var localization);

        var settings = new WebUiSettings(ctx.Config);
        var backend = new AppBackend(ctx, shell, library, localization, StringKeys(ctx));
        var events = new WebEventHub();
        var libraryApi = new LibraryApi(library, ctx.Get<IThumbnailService>(), ctx.Get<IImagingService>(), TimeProvider.System);
        var sessions = new SessionStore(Path.Combine(ctx.DataDirectory, "sessions.json"), TimeProvider.System);
        var controller = new WebUiController(settings, backend, events, ctx.Log,
                                             Path.Combine(ctx.PluginDirectory, "www"),
                                             WebUiSettings.PortOverride(ctx.Environment.CommandLineArgs),
                                             sessions, new LoginThrottle(TimeProvider.System),
                                             new CertificateStore(ctx.DataDirectory, settings), libraryApi);
        controller.SignInPaused += () => ctx.Ui.Post(() => shell.Notify(Loc.Get("WebUi.Error.SignInPaused"), NotifySeverity.Warning));

        // Open tabs follow the app live.
        ctx.On<LanguageChanged>(e => events.Publish("language", new { language = e.Language }));
        ctx.On<ThemeChanged>(e => events.Publish("theme", new { theme = AppBackend.ThemeName(e.Theme) }));
        ctx.On<LibraryChanged>(e =>
        {
            libraryApi.Invalidate();
            events.Publish("library", new { kind = e.Kind });
        });

        // The "Web" page follows its setting while the plugin runs; it closes before the server stops.
        var page = new WebPageHost(ctx, shell, () => new WebPageView(new WebPageViewModel(ctx, controller, shell)));
        page.Apply(settings.ShowPage);

        ctx.Get<ISettingsService>().AddSection(ctx, new SettingsSection
        {
            Id = SectionId,
            TitleKey = "WebUi.Settings.Title",
            Glyph = "", // Segoe Fluent Icons: Globe
            Order = 500,
            CreateView = () => new WebUiSettingsView { DataContext = new WebUiSettingsViewModel(ctx, controller, shell, page) },
        });

        // Stopping must not block the UI thread; a quick re-enable retries the port until the old server let it go.
        ctx.Track(() => _ = StopAsync(controller, libraryApi));
        ctx.Track(page);
        _ = StartAsync(ctx, controller, shell);
    }

    private static async Task StopAsync(WebUiController controller, LibraryApi libraryApi)
    {
        await controller.DisposeAsync();
        await libraryApi.DisposeAsync(); // After the server: no request still reads an open book.
    }

    private static async Task StartAsync(IPluginContext ctx, WebUiController controller, IShellService shell)
    {
        try
        {
            if (await Task.Run(controller.StartAsync) == WebServerState.PortInUse && !ctx.Deactivating.IsCancellationRequested)
            {
                ctx.Ui.Post(() => shell.Notify(Loc.Format("WebUi.Error.PortInUse", controller.Port), NotifySeverity.Error));
            }
        }
        catch (Exception ex)
        {
            ctx.Log.Error("Web interface could not start.", ex);
        }
    }

    /// <summary>The keys of this plugin's English language file: the strings the web page may ask for.</summary>
    private static IReadOnlyDictionary<string, string> StringKeys(IPluginContext ctx)
    {
        var path = Path.Combine(ctx.PluginDirectory, "lang", "en.json");
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ctx.Log.Warn("Web interface: could not read " + path, ex);
            return new Dictionary<string, string>();
        }
    }
}

/// <summary>Reads the app's state for the web server; every read runs on the UI thread.</summary>
internal sealed class AppBackend(IPluginContext ctx, IShellService shell, ILibraryService library,
                                 ILocalizationService? localization, IReadOnlyDictionary<string, string> english) : IWebUiBackend
{
    /// <summary>The API name of a theme.</summary>
    /// <param name="theme">App theme.</param>
    public static string ThemeName(AppTheme theme) => theme switch
    {
        AppTheme.Light => "light",
        AppTheme.Dark => "dark",
        _ => "system",
    };

    public async Task<WebState> GetStateAsync()
    {
        WebState? state = null;
        await ctx.Ui.InvokeAsync(() => state = new WebState(
            localization?.CurrentLanguage ?? "en",
            ThemeName(shell.Theme),
            [.. library.Kinds.Select(k => new WebKind(k.Id, Text(k.DisplayNameKey)))]));
        return state!;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetStringsAsync()
    {
        Dictionary<string, string>? strings = null;
        await ctx.Ui.InvokeAsync(() => strings = english.ToDictionary(p => p.Key, p => localization?.Get(p.Key) ?? p.Value));
        return strings!;
    }

    private string Text(string key) => localization?.Get(key) ?? key;
}
