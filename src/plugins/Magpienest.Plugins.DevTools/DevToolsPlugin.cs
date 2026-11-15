using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Thumbnails;

namespace Magpienest.Plugins.DevTools;

/// <summary>
/// <c>--screenshot &lt;dir&gt; [--pages a,b] [--lang code] [--theme light|dark|system]</c> (architecture §14).
/// Does nothing when the argument is absent.
/// </summary>
[Requires(typeof(IShellService))]
[Optional(typeof(ILocalizationService), typeof(ILibraryService), typeof(IThumbnailService))]
public sealed class DevToolsPlugin : Plugin
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(90);

    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var options = ScreenshotOptions.Parse(ctx.Environment.CommandLineArgs);
        if (options is null)
        {
            return;
        }

        var started = false;
        void Begin()
        {
            if (!started)
            {
                started = true;
                _ = RunAsync(ctx, options);
            }
        }

        ctx.On<AppStarted>(_ => Begin());
        if (ctx.Environment.IsStarted)
        {
            Begin();
        }
    }

    private static async Task RunAsync(IPluginContext ctx, ScreenshotOptions options)
    {
        var shell = ctx.Get<IShellService>();
        ctx.TryGet<ILocalizationService>(out var localization);
        var dispatcher = Application.Current.Dispatcher;
        var exitCode = 0;

        _ = Task.Delay(Watchdog, ctx.Deactivating).ContinueWith(
            t =>
            {
                if (!t.IsCanceled)
                {
                    ctx.Log.Error($"Screenshot run did not finish within {Watchdog.TotalSeconds} s.");
                    ctx.Ui.Post(() => ctx.Environment.RequestShutdown(1));
                }
            },
            TaskScheduler.Default);

        var previousLanguage = localization?.LanguageSetting;
        var previousTheme = shell.Theme;
        var previousPage = shell.CurrentPageId;
        try
        {
            await Idle(dispatcher);
            if (options.Language is not null)
            {
                if (localization is null)
                {
                    ctx.Log.Warn("--lang ignored: no localization service.");
                }
                else
                {
                    localization.LanguageSetting = options.Language;
                }
            }

            if (options.Theme is { } theme)
            {
                shell.Theme = theme;
            }

            Directory.CreateDirectory(options.OutputDirectory);
            if (options.Sources.Count > 0 && !await AddSourcesAsync(ctx, options.Sources))
            {
                exitCode = 1;
            }

            var pages = options.Pages ?? [.. shell.Pages.Select(p => p.Id)];
            if (pages.Count == 0)
            {
                // No page registered: capture whatever the window shows (the empty state).
                await Capture(ctx, shell, dispatcher, Path.Combine(options.OutputDirectory, "empty.png"));
            }

            foreach (var entry in pages)
            {
                // "page" or "page:parameter" (e.g. settings:plugins selects that settings section).
                var separator = entry.IndexOf(':', StringComparison.Ordinal);
                var id = separator < 0 ? entry : entry[..separator];
                var parameter = separator < 0 ? null : entry[(separator + 1)..];
                if (!shell.Navigate(id, parameter))
                {
                    ctx.Log.Error($"Screenshot: page '{id}' is not registered.");
                    exitCode = 1;
                    continue;
                }

                await Idle(dispatcher);
                await Task.Delay(options.Sources.Count > 0 ? 2000 : 500, ctx.Deactivating);
                await Idle(dispatcher);
                var file = parameter is null ? id : $"{id}-{parameter}";
                if (!await Capture(ctx, shell, dispatcher, Path.Combine(options.OutputDirectory, file + ".png")))
                {
                    exitCode = 1;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            ctx.Log.Error("Screenshot run failed.", ex);
            exitCode = 1;
        }
        finally
        {
            if (!ctx.Deactivating.IsCancellationRequested)
            {
                // --lang and --theme are not persisted: put the previous choices back.
                if (localization is not null && previousLanguage is not null)
                {
                    localization.LanguageSetting = previousLanguage;
                }

                shell.Theme = previousTheme;
                if (previousPage is not null)
                {
                    shell.Navigate(previousPage);
                }
            }
        }

        ctx.Log.Info($"Screenshot run finished with exit code {exitCode}; output '{options.OutputDirectory}'.");
        ctx.Environment.RequestShutdown(exitCode);
    }

    /// <summary>
    /// <c>--add-source kind=path</c>: adds the folder (or reuses it), scans it and warms the thumbnails of the first
    /// items so the captured grid shows content.
    /// </summary>
    private static async Task<bool> AddSourcesAsync(IPluginContext ctx, IReadOnlyList<(string Kind, string Path)> sources)
    {
        if (!ctx.TryGet<ILibraryService>(out var library))
        {
            ctx.Log.Error("--add-source: no library service.");
            return false;
        }

        ctx.TryGet<IThumbnailService>(out var thumbnails);
        var ok = true;
        foreach (var (kind, path) in sources)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var source = (await library.GetSourcesAsync(kind))
                                 .FirstOrDefault(s => string.Equals(s.Path, full.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                             ?? await library.AddSourceAsync(kind, full);
                var result = await library.ScanAsync(source.Id, null, ctx.Deactivating);
                ctx.Log.Info($"--add-source {kind}={full}: {result.Added} added, {result.Total} total.");
                if (thumbnails is not null)
                {
                    var items = await library.QueryAsync(new ItemQuery { Kind = kind, Limit = 80 }, ctx.Deactivating);
                    await Task.WhenAll(items.Select(item => thumbnails.GetAsync(item, ctx.Deactivating)));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.Log.Error($"--add-source {kind}={path} failed.", ex);
                ok = false;
            }
        }

        return ok;
    }

    private static async Task<bool> Capture(IPluginContext ctx, IShellService shell, Dispatcher dispatcher, string path)
    {
        await Idle(dispatcher);
        var window = shell.MainWindow;
        if (window.Content is not FrameworkElement content || content.ActualWidth <= 0 || content.ActualHeight <= 0)
        {
            ctx.Log.Error("Screenshot: the main window has no visible content.");
            return false;
        }

        var dpi = VisualTreeHelper.GetDpi(content);
        var width = content.ActualWidth;
        var height = content.ActualHeight;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * dpi.DpiScaleX),
            (int)Math.Ceiling(height * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);

        // The window background is not part of the content; paint it first so the PNG matches the screen.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, width, height);
            dc.DrawRectangle(window.Background, null, bounds);
            dc.DrawRectangle(new VisualBrush(content), null, bounds);
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        ctx.Log.Info($"Screenshot saved: '{path}'.");
        return true;
    }

    private static async Task Idle(Dispatcher dispatcher) =>
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
}

/// <summary>Parsed screenshot arguments.</summary>
internal sealed record ScreenshotOptions(string OutputDirectory, IReadOnlyList<string>? Pages, string? Language, AppTheme? Theme,
                                         IReadOnlyList<(string Kind, string Path)> Sources)
{
    public static ScreenshotOptions? Parse(IReadOnlyList<string> args)
    {
        string? Value(string name)
        {
            for (var i = 0; i < args.Count - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        var output = Value("--screenshot");
        if (output is null)
        {
            return null;
        }

        var pages = Value("--pages")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        AppTheme? theme = Enum.TryParse<AppTheme>(Value("--theme"), ignoreCase: true, out var parsed) ? parsed : null;
        var sources = new List<(string Kind, string Path)>();
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--add-source", StringComparison.OrdinalIgnoreCase)
                && args[i + 1].Split('=', 2) is [{ Length: > 0 } kind, { Length: > 0 } path])
            {
                sources.Add((kind, path));
            }
        }

        return new ScreenshotOptions(Path.GetFullPath(output), pages, Value("--lang"), theme, sources);
    }
}
