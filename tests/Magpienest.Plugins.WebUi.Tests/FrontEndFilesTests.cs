using System.Text.RegularExpressions;

namespace Magpienest.Plugins.WebUi.Tests;

/// <summary>T7.5: the web pages load only files that exist in <c>www/</c> and only types the server sends.</summary>
public sealed partial class FrontEndFilesTests
{
    private static readonly string Www = Path.Combine(FindRepoRoot(), "src", "plugins", "Magpienest.Plugins.WebUi", "www");

    [Fact]
    public void EveryScriptStylesheetAndModuleImportExists()
    {
        var problems = new List<string>();
        foreach (var page in Directory.GetFiles(Www, "*.html"))
        {
            foreach (Match match in PageReferencePattern().Matches(File.ReadAllText(page)))
            {
                Check(page, Path.Combine(Www, match.Groups[1].Value.TrimStart('/')));
            }
        }

        foreach (var script in Directory.GetFiles(Path.Combine(Www, "js"), "*.js"))
        {
            foreach (Match match in ImportPattern().Matches(File.ReadAllText(script)))
            {
                Check(script, Path.Combine(Path.GetDirectoryName(script)!, match.Groups[1].Value));
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        void Check(string from, string target)
        {
            if (!File.Exists(target))
            {
                problems.Add($"{Path.GetFileName(from)} -> {target}");
            }
        }
    }

    [Fact]
    public void EveryFileInWwwIsServable()
    {
        var unservable = Directory.GetFiles(Www, "*", SearchOption.AllDirectories)
            .Where(f => StaticFiles.ContentType(Path.GetExtension(f)) is null)
            .ToList();

        Assert.Empty(unservable);
    }

    [Fact]
    public void ThePagesUseNoInlineScriptOrStyle()
    {
        // The Content-Security-Policy allows only 'self': inline code and style attributes would be blocked.
        foreach (var page in Directory.GetFiles(Www, "*.html"))
        {
            var html = File.ReadAllText(page);
            Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)[^>]*>", html);
            Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(@"\s(style|on[a-z]+)=", html);
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Magpienest.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Magpienest.slnx not found above the test folder.");
    }

    [GeneratedRegex(@"<(?:script|link)\b[^>]*\b(?:src|href)=""(/[^""]+\.(?:js|css))""")]
    private static partial Regex PageReferencePattern();

    [GeneratedRegex(@"\bfrom\s+""(\./[^""]+)""")]
    private static partial Regex ImportPattern();
}
