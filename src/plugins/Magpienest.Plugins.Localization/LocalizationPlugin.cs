using System.Globalization;
using System.IO;
using System.Text.Json;
using Magpienest.Sdk;
using Magpienest.Sdk.Localization;

namespace Magpienest.Plugins.Localization;

/// <summary>Provides <see cref="ILocalizationService"/> and loads every active plugin's lang folder.</summary>
public sealed class LocalizationPlugin : Plugin
{
    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var systemCulture = CultureInfo.CurrentUICulture;
        var service = new LocalizationService(ctx.Config, ctx.Log, () => systemCulture, language => ctx.Emit(new LanguageChanged(language)));

        service.LoadDirectory(ctx.Manifest.Id, Path.Combine(ctx.PluginDirectory, "lang"));

        // Plugins activated before this one: find their folders by id in the two plugin roots.
        var active = ctx.Get<IPluginManager>().Plugins
            .Where(p => p.State == PluginState.Active && p.Manifest.Id != ctx.Manifest.Id)
            .Select(p => p.Manifest.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (active.Count > 0)
        {
            foreach (var (id, directory) in FindPluginDirectories(ctx))
            {
                if (active.Contains(id))
                {
                    service.LoadDirectory(id, Path.Combine(directory, "lang"));
                }
            }
        }

        service.LoadDirectory(LocalizationService.UserOrigin, Path.Combine(ctx.Environment.DataDirectory, "lang"));

        ctx.On<PluginActivating>(e => service.LoadDirectory(e.Manifest.Id, Path.Combine(e.PluginDirectory, "lang")));
        ctx.On<PluginStateChanged>(e =>
        {
            if (e.Plugin.State != PluginState.Active && e.Plugin.Manifest.Id != ctx.Manifest.Id)
            {
                service.Unload(e.Plugin.Manifest.Id);
            }
        });

        ctx.Provide<ILocalizationService>(service);
        LocSource.Instance.SetProvider(service);
        ctx.Track(() => LocSource.Instance.SetProvider(null));
    }

    private static IEnumerable<(string Id, string Directory)> FindPluginDirectories(IPluginContext ctx)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in new[] { Path.Combine(ctx.Environment.AppDirectory, "plugins"), Path.Combine(ctx.Environment.DataDirectory, "plugins") })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.GetDirectories(root))
            {
                try
                {
                    var manifest = Path.Combine(directory, "plugin.json");
                    if (!File.Exists(manifest))
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(File.ReadAllText(manifest), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    if (document.RootElement.TryGetProperty("id", out var id) && id.GetString() is { } value)
                    {
                        found.TryAdd(value, directory);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    ctx.Log.Warn($"Could not read the manifest in '{directory}'.", ex);
                }
            }
        }

        return found.Select(p => (p.Key, p.Value));
    }
}
