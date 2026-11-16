using System.Globalization;
using System.IO;
using System.Text.Json;
using Magpienest.Sdk;
using Magpienest.Sdk.Localization;

namespace Magpienest.Plugins.Localization;

/// <summary>Language tables per origin (plugin id, folder or "user"), merged into one view.</summary>
internal sealed class LocalizationService : ILocalizationService
{
    internal const string UserOrigin = "user";
    private const string ConfigKey = "language";
    private const string SystemSetting = "system";
    private const string Fallback = "en";

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IPluginConfig _config;
    private readonly ILog _log;
    private readonly Func<CultureInfo> _systemCulture;
    private readonly Action<string> _languageChanged;
    private readonly Lock _lock = new();

    // origin → language → key → text; insertion order of origins is kept, "user" is always merged last.
    private readonly List<(string Origin, Dictionary<string, Dictionary<string, string>> Tables)> _origins = [];

    private volatile Dictionary<string, Dictionary<string, string>> _merged = [];
    private volatile string[] _chain = [Fallback];
    private string _setting;
    private string _current = Fallback;

    public LocalizationService(IPluginConfig config, ILog log, Func<CultureInfo> systemCulture, Action<string> languageChanged)
    {
        _config = config;
        _log = log;
        _systemCulture = systemCulture;
        _languageChanged = languageChanged;
        _setting = config.Get(ConfigKey, SystemSetting);
    }

    public string CurrentLanguage => _current;

    public string LanguageSetting
    {
        get => _setting;
        set
        {
            var setting = string.IsNullOrWhiteSpace(value) ? SystemSetting : value.Trim();
            _setting = setting;
            _config.Set(ConfigKey, setting);
            UpdateCurrent(notify: true);
        }
    }

    public IReadOnlyList<LanguageInfo> AvailableLanguages
    {
        get
        {
            var codes = _merged.Keys.ToList();
            return [.. codes.Select(code => new LanguageInfo(code, NativeName(code))).OrderBy(l => l.NativeName, StringComparer.CurrentCulture)];
        }
    }

    public string Get(string key)
    {
        var merged = _merged;
        foreach (var language in _chain)
        {
            if (merged.TryGetValue(language, out var table) && table.TryGetValue(key, out var text))
            {
                return text;
            }
        }

        return key;
    }

    public string Format(string key, params object?[] args)
    {
        var text = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, args);
        }
        catch (FormatException ex)
        {
            _log.Warn($"Localized text '{key}' has an invalid format.", ex);
            return text;
        }
    }

    public void AddDirectory(IPluginContext owner, string directory)
    {
        var origin = "dir:" + Path.GetFullPath(directory);
        LoadDirectory(origin, directory);
        owner.Track(() => Unload(origin));
    }

    /// <summary>Loads every &lt;code&gt;.json in <paramref name="directory"/> as <paramref name="origin"/>, replacing a previous load.</summary>
    public void LoadDirectory(string origin, string directory)
    {
        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.GetFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                var code = Path.GetFileNameWithoutExtension(file);
                var table = ReadFile(file);
                if (table is not null)
                {
                    tables[code] = table;
                }
            }
        }

        lock (_lock)
        {
            _origins.RemoveAll(o => o.Origin == origin);
            _origins.Add((origin, tables));
            Rebuild();
        }
    }

    public void Unload(string origin)
    {
        lock (_lock)
        {
            if (_origins.RemoveAll(o => o.Origin == origin) > 0)
            {
                Rebuild();
            }
        }
    }

    public bool IsLoaded(string origin)
    {
        lock (_lock)
        {
            return _origins.Any(o => o.Origin == origin);
        }
    }

    /// <summary>Maps a culture to an available language: zh-TW/HK/MO/Hant → zh-TW, then exact, parent, en.</summary>
    internal string Resolve(string setting)
    {
        var name = string.Equals(setting, SystemSetting, StringComparison.OrdinalIgnoreCase) ? _systemCulture().Name : setting;
        if (IsTraditionalChinese(name))
        {
            name = "zh-TW";
        }

        var available = _merged;
        for (var culture = TryCulture(name); culture is not null && culture.Name.Length > 0; culture = culture.Parent)
        {
            var match = available.Keys.FirstOrDefault(k => string.Equals(k, culture.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return Fallback;
    }

    private void Rebuild()
    {
        var merged = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<(string, string), string>();
        foreach (var (origin, tables) in _origins.OrderBy(o => o.Origin == UserOrigin ? 1 : 0))
        {
            foreach (var (language, table) in tables)
            {
                if (!merged.TryGetValue(language, out var target))
                {
                    target = new Dictionary<string, string>(StringComparer.Ordinal);
                    merged[language] = target;
                }

                foreach (var (key, text) in table)
                {
                    if (origin != UserOrigin && owners.TryGetValue((language, key), out var previous) && previous != origin)
                    {
                        _log.Warn($"Duplicate localization key '{key}' ({language}) in '{previous}' and '{origin}'; using '{origin}'.");
                    }

                    owners[(language, key)] = origin;
                    target[key] = text;
                }
            }
        }

        _merged = merged;
        UpdateCurrent(notify: false);
    }

    private void UpdateCurrent(bool notify)
    {
        var current = Resolve(_setting);
        var changed = current != _current;
        _current = current;
        _chain = BuildChain(current);
        if (notify || changed)
        {
            LocSource.Instance.RaiseChanged();
            _languageChanged(current);
        }
    }

    private static string[] BuildChain(string language)
    {
        var chain = new List<string>();
        for (var culture = TryCulture(language); culture is not null && culture.Name.Length > 0; culture = culture.Parent)
        {
            chain.Add(culture.Name);
        }

        if (chain.Count == 0)
        {
            chain.Add(language);
        }

        if (!chain.Contains(Fallback, StringComparer.OrdinalIgnoreCase))
        {
            chain.Add(Fallback);
        }

        return [.. chain];
    }

    private Dictionary<string, string>? ReadFile(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The root is not an object.");
            }

            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    table[property.Name] = property.Value.GetString()!;
                }
                else
                {
                    _log.Warn($"Ignoring non-string value '{property.Name}' in '{file}'.");
                }
            }

            return table;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Error($"Could not read language file '{file}'; skipped.", ex);
            return null;
        }
    }

    private static bool IsTraditionalChinese(string name) =>
        name.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
        || name.Equals("zh-HK", StringComparison.OrdinalIgnoreCase)
        || name.Equals("zh-MO", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase);

    private static CultureInfo? TryCulture(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static string NativeName(string code) => TryCulture(code)?.NativeName ?? code;
}
