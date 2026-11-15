using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace Magpienest.Sdk.Localization;

/// <summary>Provided by the Localization plugin: localized text and the language setting.</summary>
public interface ILocalizationService
{
    /// <summary>Effective language code, e.g. "zh-TW".</summary>
    string CurrentLanguage { get; }

    /// <summary>"system" or a language code; setting it persists and switches live.</summary>
    string LanguageSetting { get; set; }

    /// <summary>Languages that have at least one language file.</summary>
    IReadOnlyList<LanguageInfo> AvailableLanguages { get; }

    /// <summary>Returns the text for a key (current language → parent → en → the key itself).</summary>
    /// <param name="key">Localization key.</param>
    string Get(string key);

    /// <summary>Returns the formatted text for a key.</summary>
    /// <param name="key">Localization key.</param>
    /// <param name="args">Format arguments.</param>
    string Format(string key, params object?[] args);

    /// <summary>For language folders outside the automatic &lt;plugin&gt;/lang location.</summary>
    /// <param name="owner">Registering plugin; the folder is unloaded when it deactivates.</param>
    /// <param name="directory">Folder containing &lt;code&gt;.json files.</param>
    void AddDirectory(IPluginContext owner, string directory);
}

/// <summary>A language with at least one language file.</summary>
/// <param name="Code">BCP-47 code, e.g. "zh-TW".</param>
/// <param name="NativeName">Name in that language, from CultureInfo.</param>
public sealed record LanguageInfo(string Code, string NativeName);

/// <summary>Kernel-bus event: the effective language changed.</summary>
/// <param name="Language">The new effective language code.</param>
public sealed record LanguageChanged(string Language);

/// <summary>Static bridge used by XAML and by code that has no context at hand.</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    private ILocalizationService? _provider;

    private LocSource() { }

    /// <summary>The single instance.</summary>
    public static LocSource Instance { get; } = new();

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The text for a key, or the key itself when no provider is set.</summary>
    /// <param name="key">Localization key.</param>
    public string this[string key] => _provider?.Get(key) ?? key;

    /// <summary>The current provider, if any.</summary>
    internal ILocalizationService? Provider => _provider;

    /// <summary>Called only by the Localization plugin.</summary>
    /// <param name="provider">The provider, or null on deactivation.</param>
    public void SetProvider(ILocalizationService? provider)
    {
        _provider = provider;
        RaiseChanged();
    }

    /// <summary>Raises PropertyChanged("Item[]") so every binding re-reads its text.</summary>
    public void RaiseChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// <summary>Static helpers forwarding to <see cref="LocSource.Instance"/>.</summary>
public static class Loc
{
    /// <summary>The text for a key.</summary>
    /// <param name="key">Localization key.</param>
    public static string Get(string key) => LocSource.Instance[key];

    /// <summary>The formatted text for a key.</summary>
    /// <param name="key">Localization key.</param>
    /// <param name="args">Format arguments.</param>
    public static string Format(string key, params object?[] args)
    {
        var provider = LocSource.Instance.Provider;
        if (provider is not null)
        {
            return provider.Format(key, args);
        }

        try
        {
            return string.Format(System.Globalization.CultureInfo.CurrentCulture, key, args);
        }
        catch (FormatException)
        {
            return key;
        }
    }
}

/// <summary>XAML markup extension: <c>{sdk:Loc Some.Key}</c>. Updates live on language change.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    /// <summary>Creates an extension without a key.</summary>
    public LocExtension() => Key = string.Empty;

    /// <summary>Creates the extension.</summary>
    /// <param name="key">Localization key.</param>
    public LocExtension(string key) => Key = key;

    /// <summary>Localization key.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; }

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = LocSource.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
