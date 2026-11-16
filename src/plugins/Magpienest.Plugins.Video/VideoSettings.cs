using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk;

namespace Magpienest.Plugins.Video;

/// <summary>The plugin's stored settings (config/magpienest.video.json).</summary>
internal sealed class VideoSettings(IPluginConfig config)
{
    internal const string ExtensionsKey = "extensions";
    internal const string ContinueNextKey = "continueNext";
    internal const string VolumeKey = "volume";
    internal const string MutedKey = "muted";
    internal const string RateKey = "rate";
    internal const string RememberKey = "rememberPosition";

    /// <summary>Default scanned extensions (phase 3 spec).</summary>
    internal static readonly string[] DefaultExtensions =
    [
        ".mp4", ".m4v", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".asf", ".flv", ".f4v", ".mpg", ".mpeg",
        ".m2ts", ".mts", ".ts", ".vob", ".ogv", ".3gp", ".rm", ".rmvb", ".divx",
    ];

    /// <summary>Lower-case extensions with a leading dot.</summary>
    public IReadOnlySet<string> Extensions
    {
        get
        {
            var stored = config.Get<string[]?>(ExtensionsKey, null);
            return new HashSet<string>(stored is { Length: > 0 } ? stored : DefaultExtensions, StringComparer.OrdinalIgnoreCase);
        }
    }

    public string[] ExtensionList => [.. Extensions.Order(StringComparer.Ordinal)];

    public void SetExtensions(IReadOnlyCollection<string> extensions) =>
        config.Set<string[]?>(ExtensionsKey, extensions.Count == 0 ? null : [.. extensions]);

    public bool ContinueWithNext
    {
        get => config.Get(ContinueNextKey, false);
        set => config.Set(ContinueNextKey, value);
    }

    /// <summary>
    /// Remember where each video stopped and resume there; also keeps the watched status and the progress bars.
    /// Off: every video starts at the beginning and playback records nothing but "last opened".
    /// </summary>
    public bool RememberPosition
    {
        get => config.Get(RememberKey, true);
        set => config.Set(RememberKey, value);
    }

    public int Volume
    {
        get => Math.Clamp(config.Get(VolumeKey, 100), 0, 100);
        set => config.Set(VolumeKey, Math.Clamp(value, 0, 100));
    }

    public bool Muted
    {
        get => config.Get(MutedKey, false);
        set => config.Set(MutedKey, value);
    }

    public double Rate
    {
        get => Math.Clamp(config.Get(RateKey, 1.0), 0.25, 4.0);
        set => config.Set(RateKey, Math.Clamp(value, 0.25, 4.0));
    }

    /// <summary>
    /// Parses user text such as "mp4, .MKV avi" into distinct ".ext" values; separators are spaces, commas and
    /// semicolons. Invalid parts (containing path characters) are dropped.
    /// </summary>
    public static IReadOnlyList<string> ParseExtensions(string? text)
    {
        var result = new List<string>();
        foreach (var part in (text ?? "").Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var ext = "." + part.Trim().TrimStart('.', '*').ToLowerInvariant();
            if (ext.Length > 1 && ext.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|', '.'], 1) < 0 &&
                !result.Contains(ext))
            {
                result.Add(ext);
            }
        }

        return result;
    }
}

/// <summary>The "Videos" settings section.</summary>
internal sealed partial class VideoSettingsViewModel : ObservableObject
{
    private readonly VideoSettings _settings;

    public VideoSettingsViewModel(VideoSettings settings)
    {
        _settings = settings;
        _extensions = string.Join(" ", settings.ExtensionList);
        _continueWithNext = settings.ContinueWithNext;
        _rememberPosition = settings.RememberPosition;
    }

    [ObservableProperty]
    private bool _rememberPosition;

    partial void OnRememberPositionChanged(bool value) => _settings.RememberPosition = value;

    [ObservableProperty]
    private string _extensions;

    [ObservableProperty]
    private bool _continueWithNext;

    partial void OnExtensionsChanged(string value)
    {
        var parsed = VideoSettings.ParseExtensions(value);
        _settings.SetExtensions(parsed);
    }

    partial void OnContinueWithNextChanged(bool value) => _settings.ContinueWithNext = value;

    [RelayCommand]
    private void ResetExtensions()
    {
        _settings.SetExtensions([]);
        Extensions = string.Join(" ", _settings.ExtensionList);
    }
}
