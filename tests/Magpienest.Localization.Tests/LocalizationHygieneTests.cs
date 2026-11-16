using System.Text.Json;
using System.Text.RegularExpressions;

namespace Magpienest.Localization.Tests;

/// <summary>Source-level checks of the localization rules (architecture §10). No project references.</summary>
public sealed partial class LocalizationHygieneTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Src = Path.Combine(RepoRoot, "src");
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    [Fact]
    public void EveryEnglishFileHasATraditionalChineseTwinWithTheSameKeys()
    {
        var problems = new List<string>();
        var englishFiles = PluginLangFiles("en.json").ToList();
        Assert.NotEmpty(englishFiles);
        foreach (var en in englishFiles)
        {
            var zh = Path.Combine(Path.GetDirectoryName(en)!, "zh-TW.json");
            if (!File.Exists(zh))
            {
                problems.Add($"{Relative(en)}: missing zh-TW.json");
                continue;
            }

            var enKeys = Keys(en);
            var zhKeys = Keys(zh);
            problems.AddRange(enKeys.Except(zhKeys).Select(k => $"{Relative(zh)}: missing key '{k}'"));
            problems.AddRange(zhKeys.Except(enKeys).Select(k => $"{Relative(en)}: missing key '{k}'"));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryKeyUsedInSourceExistsInEnglish()
    {
        var known = PluginLangFiles("en.json").SelectMany(Keys).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var file in SourceFiles("*.xaml"))
        {
            foreach (Match match in XamlLocPattern().Matches(File.ReadAllText(file)))
            {
                Check(file, match.Groups[1].Value);
            }
        }

        foreach (var file in SourceFiles("*.cs"))
        {
            foreach (Match match in CodeKeyPattern().Matches(File.ReadAllText(file)))
            {
                Check(file, match.Groups[1].Value);
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        void Check(string file, string key)
        {
            if (!known.Contains(key))
            {
                problems.Add($"{Relative(file)}: key '{key}' is not in any en.json");
            }
        }
    }

    [Fact]
    public void EveryManifestHasEnglishAndChineseNameAndDescription()
    {
        var problems = new List<string>();
        var manifests = Directory.GetFiles(Path.Combine(Src, "plugins"), "plugin.json", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(manifests);
        foreach (var manifest in manifests)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest), JsonOptions);
            foreach (var property in new[] { "name", "description" })
            {
                foreach (var language in new[] { "en", "zh-TW" })
                {
                    if (!document.RootElement.TryGetProperty(property, out var texts)
                        || !texts.TryGetProperty(language, out var text)
                        || string.IsNullOrWhiteSpace(text.GetString()))
                    {
                        problems.Add($"{Relative(manifest)}: missing {property}.{language}");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void XamlHasNoLiteralText()
    {
        var problems = new List<string>();
        foreach (var file in SourceFiles("*.xaml"))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in XamlLiteralPattern().Matches(lines[i]))
                {
                    var value = match.Groups[2].Value;
                    if (value != "Magpienest")
                    {
                        problems.Add($"{Relative(file)}:{i + 1}: {match.Groups[1].Value}=\"{value}\"");
                    }
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryKeyUsedInWebFilesExistsInThatPluginsEnglish()
    {
        var problems = new List<string>();
        var checkedFiles = 0;
        foreach (var www in WebFolders())
        {
            var en = Path.Combine(Path.GetDirectoryName(www)!, "lang", "en.json");
            var known = File.Exists(en) ? Keys(en) : [];

            // Key-looking literals with the plugin's own prefix (for example "WebUi.") count too: lookup tables of keys.
            var prefixes = known.Select(k => k.Split('.')[0] + ".").ToHashSet(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(www, "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
                                     f.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
            {
                checkedFiles++;
                var text = File.ReadAllText(file);
                var used = WebKeyPattern().Matches(text).Select(m => m.Groups[1].Value)
                    .Concat(WebLiteralPattern().Matches(text).Select(m => m.Groups[1].Value)
                                .Where(v => prefixes.Any(p => v.StartsWith(p, StringComparison.Ordinal))));
                problems.AddRange(used.Distinct().Where(k => !known.Contains(k))
                                      .Select(k => $"{Relative(file)}: key '{k}' is not in {Relative(en)}"));
            }
        }

        Assert.True(checkedFiles > 0, "No web files found.");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void WebPagesHaveNoLiteralText()
    {
        var problems = new List<string>();
        foreach (var file in WebFolders().SelectMany(w => Directory.GetFiles(w, "*.html", SearchOption.AllDirectories)))
        {
            var html = HtmlSkippedPattern().Replace(File.ReadAllText(file), "");
            problems.AddRange(HtmlTextPattern().Matches(html).Select(m => m.Groups[1].Value.Trim())
                                  .Where(text => text.Any(char.IsLetter))
                                  .Select(text => $"{Relative(file)}: text \"{text}\""));
            problems.AddRange(HtmlAttributePattern().Matches(html)
                                  .Select(m => $"{Relative(file)}: {m.Groups[1].Value}=\"{m.Groups[2].Value}\""));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static IEnumerable<string> WebFolders() =>
        Directory.GetDirectories(Path.Combine(Src, "plugins"))
            .Select(d => Path.Combine(d, "www"))
            .Where(Directory.Exists);

    private static IEnumerable<string> PluginLangFiles(string name) =>
        Directory.GetDirectories(Path.Combine(Src, "plugins"))
            .Select(d => Path.Combine(d, "lang", name))
            .Where(File.Exists);

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.GetFiles(Src, pattern, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> Keys(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file), JsonOptions);
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static string Relative(string path) => Path.GetRelativePath(RepoRoot, path);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Magpienest.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Magpienest.slnx not found above the test folder.");
    }

    [GeneratedRegex(@"\{sdk:Loc\s+([A-Za-z0-9_.\-]+)\s*\}")]
    private static partial Regex XamlLocPattern();

    [GeneratedRegex(@"(?:Loc\.Get\(""|Loc\.Format\(""|TitleKey\s*=\s*""|DisplayNameKey\s*=\s*"")([^""]+)""")]
    private static partial Regex CodeKeyPattern();

    // A literal (starting with a letter) in a text-bearing attribute. Glyphs (&#x…;) and bindings ({…}) do not match.
    [GeneratedRegex(@"\b(Text|Content|Header|ToolTip)=""(\p{L}[^""]*)""")]
    private static partial Regex XamlLiteralPattern();

    // t("key", …) in scripts, and data-i18n or data-i18n-<attribute> in markup and scripts.
    [GeneratedRegex(@"(?:\bt\(\s*[""']|data-i18n(?:-[a-z-]+)?=[""'])([^""']+)[""']")]
    private static partial Regex WebKeyPattern();

    // A quoted dotted name such as "WebUi.Web.Back".
    [GeneratedRegex(@"[""']([A-Z][A-Za-z0-9]*(?:\.[A-Za-z0-9_]+){2,})[""']")]
    private static partial Regex WebLiteralPattern();

    // Script and style bodies and comments may hold anything.
    [GeneratedRegex(@"<script\b[^>]*>.*?</script>|<style\b[^>]*>.*?</style>|<!--.*?-->", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlSkippedPattern();

    [GeneratedRegex(@">([^<]+)<")]
    private static partial Regex HtmlTextPattern();

    // Visible attribute text must come from data-i18n-<attribute>.
    [GeneratedRegex(@"\s(placeholder|title|aria-label|alt)=""(\p{L}[^""]*)""")]
    private static partial Regex HtmlAttributePattern();
}
