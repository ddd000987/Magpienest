using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Magpienest.Plugins.WebUi;

/// <summary>What the web server needs from the app. The plugin's implementation hops to the UI thread; tests use a fake.</summary>
internal interface IWebUiBackend
{
    /// <summary>Current language, theme and registered kinds.</summary>
    Task<WebState> GetStateAsync();

    /// <summary>Every key of this plugin's <c>en.json</c> with its text in the current language.</summary>
    Task<IReadOnlyDictionary<string, string>> GetStringsAsync();
}

/// <summary><c>GET /api/state</c>.</summary>
/// <param name="Language">Language code, for example "zh-TW".</param>
/// <param name="Theme"><c>system</c>, <c>light</c> or <c>dark</c>.</param>
/// <param name="Kinds">Registered item kinds with localized names.</param>
internal sealed record WebState(string Language, string Theme, IReadOnlyList<WebKind> Kinds);

/// <summary>One item kind.</summary>
/// <param name="Id">Kind id.</param>
/// <param name="Name">Localized name.</param>
internal sealed record WebKind(string Id, string Name);

/// <summary>JSON settings of the web API (camelCase).</summary>
internal static class WebJson
{
    /// <summary>Shared, immutable serializer options.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        // Chinese and other non-Latin text stays readable; characters that matter to HTML are still escaped.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };
}
