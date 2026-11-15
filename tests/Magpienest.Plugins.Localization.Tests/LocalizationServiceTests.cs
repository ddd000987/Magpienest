using System.Globalization;
using Magpienest.Sdk;

namespace Magpienest.Plugins.Localization.Tests;

public sealed class LocalizationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "magpienest-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void FallbackChainCurrentThenParentThenEnglishThenKey()
    {
        var plugin = Folder("plugin", ("en", """{ "A": "A-en", "B": "B-en" }"""), ("zh-TW", """{ "A": "A-zh" }"""), ("fr", """{ "A": "A-fr" }"""));
        var service = Create("zh-TW");
        service.LoadDirectory("p", plugin);

        Assert.Equal("zh-TW", service.CurrentLanguage);
        Assert.Equal("A-zh", service.Get("A"));
        Assert.Equal("B-en", service.Get("B"));
        Assert.Equal("Missing.Key", service.Get("Missing.Key"));

        service.LanguageSetting = "fr-CA";
        Assert.Equal("fr", service.CurrentLanguage);
        Assert.Equal("A-fr", service.Get("A"));
        Assert.Equal("B-en", service.Get("B"));
    }

    [Theory]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-MO", "zh-TW")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("fr-FR", "en")]
    [InlineData("en-GB", "en")]
    public void SystemCultureMapping(string system, string expected)
    {
        var plugin = Folder("plugin", ("en", "{}"), ("zh-TW", "{}"));
        var service = Create(system);
        service.LoadDirectory("p", plugin);
        Assert.Equal("system", service.LanguageSetting);
        Assert.Equal(expected, service.CurrentLanguage);
    }

    [Fact]
    public void UserOverrideWinsRegardlessOfLoadOrder()
    {
        var user = Folder("user", ("en", """{ "A": "A-user" }"""));
        var plugin = Folder("plugin", ("en", """{ "A": "A-plugin", "B": "B-plugin" }"""));
        var service = Create("en");
        service.LoadDirectory(LocalizationService.UserOrigin, user);
        service.LoadDirectory("p", plugin);

        Assert.Equal("A-user", service.Get("A"));
        Assert.Equal("B-plugin", service.Get("B"));
    }

    [Fact]
    public void UnloadRemovesKeys()
    {
        var plugin = Folder("plugin", ("en", """{ "A": "A-en" }"""));
        var service = Create("en");
        service.LoadDirectory("p", plugin);
        Assert.Equal("A-en", service.Get("A"));

        service.Unload("p");
        Assert.Equal("A", service.Get("A"));
    }

    [Fact]
    public void FormatWithArguments()
    {
        var plugin = Folder("plugin", ("en", """{ "Waiting": "Waiting for: {0} ({1})", "Broken": "Bad {0" }"""));
        var service = Create("en");
        service.LoadDirectory("p", plugin);
        Assert.Equal("Waiting for: IFoo (2)", service.Format("Waiting", "IFoo", 2));
        Assert.Equal("Bad {0", service.Format("Broken", "x"));
    }

    [Fact]
    public void LanguageSettingIsPersistedAndRaisesChange()
    {
        var plugin = Folder("plugin", ("en", "{}"), ("zh-TW", "{}"));
        var config = new MemoryConfig();
        var changes = new List<string>();
        var service = new LocalizationService(config, new NullLog(), () => CultureInfo.GetCultureInfo("en-US"), changes.Add);
        service.LoadDirectory("p", plugin);

        service.LanguageSetting = "zh-TW";
        Assert.Equal("zh-TW", config.Get("language", "system"));
        Assert.Equal("zh-TW", changes[^1]);
        Assert.Contains(service.AvailableLanguages, l => l.Code == "zh-TW");
    }

    [Fact]
    public void MalformedFileIsSkipped()
    {
        var plugin = Folder("plugin", ("en", """{ "A": "A-en" }"""), ("zh-TW", "{ not json"));
        var service = Create("zh-TW");
        service.LoadDirectory("p", plugin);
        Assert.Equal("en", service.CurrentLanguage);
        Assert.Equal("A-en", service.Get("A"));
    }

    private static LocalizationService Create(string systemCulture) =>
        new(new MemoryConfig(), new NullLog(), () => CultureInfo.GetCultureInfo(systemCulture), _ => { });

    private string Folder(string name, params (string Code, string Json)[] files)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        foreach (var (code, json) in files)
        {
            File.WriteAllText(Path.Combine(directory, code + ".json"), json);
        }

        return directory;
    }

    private sealed class MemoryConfig : IPluginConfig
    {
        private readonly Dictionary<string, object?> _values = [];

        public T Get<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value;
    }

    private sealed class NullLog : ILog
    {
        public void Debug(string message) { }

        public void Info(string message) { }

        public void Warn(string message, Exception? ex = null) { }

        public void Error(string message, Exception? ex = null) { }
    }
}
