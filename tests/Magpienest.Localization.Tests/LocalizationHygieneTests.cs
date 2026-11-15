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
}
