using Magpienest.Plugins.WebUi;

namespace Magpienest.Plugins.WebUi.Tests;

public sealed class StaticFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mn-webui-static-" + Guid.NewGuid().ToString("N"));
    private readonly StaticFiles _files;

    public StaticFilesTests()
    {
        var www = Path.Combine(_dir, "www");
        Directory.CreateDirectory(Path.Combine(www, "js"));
        Directory.CreateDirectory(Path.Combine(www, ".hidden"));
        File.WriteAllText(Path.Combine(www, "index.html"), "<p></p>");
        File.WriteAllText(Path.Combine(www, "app.css"), "body{}");
        File.WriteAllText(Path.Combine(www, "js", "main.js"), "");
        File.WriteAllText(Path.Combine(www, "logo.svg"), "<svg/>");
        File.WriteAllText(Path.Combine(www, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(www, ".hidden", "a.js"), "");
        File.WriteAllText(Path.Combine(_dir, "secret.js"), "");
        _files = new StaticFiles(www);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("/", "index.html", "text/html; charset=utf-8")]
    [InlineData("/index.html", "index.html", "text/html; charset=utf-8")]
    [InlineData("/app.css", "app.css", "text/css; charset=utf-8")]
    [InlineData("/js/main.js", "main.js", "text/javascript; charset=utf-8")]
    [InlineData("/js%2Fmain.js", "main.js", "text/javascript; charset=utf-8")]
    public void Serves_files_inside_www(string path, string file, string type)
    {
        var hit = _files.Resolve(path);
        Assert.NotNull(hit);
        Assert.Equal(file, Path.GetFileName(hit.Value.Path));
        Assert.Equal(type, hit.Value.ContentType);
    }

    [Theory]
    [InlineData("/../secret.js")]
    [InlineData("/%2e%2e/secret.js")]
    [InlineData("/js/../../secret.js")]
    [InlineData("/..%5Csecret.js")]
    [InlineData("/C:/Windows/win.ini")]
    [InlineData("/logo.svg")]
    [InlineData("/notes.txt")]
    [InlineData("/.hidden/a.js")]
    [InlineData("/missing.js")]
    [InlineData("//js/main.js")]
    [InlineData("/%zz")]
    [InlineData("relative.js")]
    public void Refuses_everything_else(string path) => Assert.Null(_files.Resolve(path));
}
