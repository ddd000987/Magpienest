using Magpienest.Core.Update;

namespace Magpienest.Core.Tests;

/// <summary>Replacing an installed Magpienest folder with a newer unpacked one.</summary>
public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "magpienest-update-" + Guid.NewGuid().ToString("N"));

    public UpdateApplierTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public void ReplacesTheProgramAndKeepsTheData()
    {
        var target = Folder("installed", new()
        {
            ["Magpienest.exe"] = "old exe",
            ["Magpienest.Core.dll"] = "old core",
            ["old-only.dll"] = "kept",
            ["portable.txt"] = "",
            [@"plugins\magpienest.shell\Shell.dll"] = "old shell",
            [@"plugins\magpienest.gone\Gone.dll"] = "removed plugin",
            [@"plugins\magpienest.shell\stale.dll"] = "stale",
            [@"data\library.db"] = "user data",
            [@"data\plugins\magpienest.webui\WebUi.dll"] = "store plugin",
        });
        var source = Folder(@"installed\data\cache\updates\0.2.0\Magpienest", new()
        {
            ["Magpienest.exe"] = "new exe",
            ["Magpienest.Core.dll"] = "new core",
            ["new-only.dll"] = "added",
            [@"plugins\magpienest.shell\Shell.dll"] = "new shell",
            [@"data\library.db"] = "must not be copied",
        });
        var backup = Path.Combine(target, @"data\cache\updates\backup");
        var log = new List<string>();

        var result = UpdateApplier.Apply(source, target, backup, log.Add);

        Assert.True(result.Success, result.Error);
        Assert.Equal("new exe", Read(target, "Magpienest.exe"));
        Assert.Equal("new core", Read(target, "Magpienest.Core.dll"));
        Assert.Equal("added", Read(target, "new-only.dll"));
        Assert.Equal("kept", Read(target, "old-only.dll"));
        Assert.True(File.Exists(Path.Combine(target, "portable.txt")));
        Assert.Equal("new shell", Read(target, @"plugins\magpienest.shell\Shell.dll"));
        Assert.False(File.Exists(Path.Combine(target, @"plugins\magpienest.shell\stale.dll")));
        Assert.False(File.Exists(Path.Combine(target, @"plugins\magpienest.gone\Gone.dll")));
        Assert.Equal("user data", Read(target, @"data\library.db"));
        Assert.Equal("store plugin", Read(target, @"data\plugins\magpienest.webui\WebUi.dll"));
        Assert.Equal("old exe", Read(backup, "Magpienest.exe"));
        Assert.Equal("stale", Read(backup, @"plugins\magpienest.shell\stale.dll"));
    }

    [Fact]
    public void ALockedFilePutsEverythingBack()
    {
        var target = Folder("installed", new()
        {
            ["Magpienest.exe"] = "old exe",
            ["a.dll"] = "old a",
            ["z.dll"] = "old z",
            [@"plugins\p\stale.dll"] = "stale",
        });
        var source = Folder("staged", new()
        {
            ["Magpienest.exe"] = "new exe",
            ["a.dll"] = "new a",
            ["b.dll"] = "new b",
            ["z.dll"] = "new z",
        });

        UpdateApplyResult result;
        using (new FileStream(Path.Combine(target, "z.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = UpdateApplier.Apply(source, target, Path.Combine(_root, "backup"), _ => { }, attempts: 2, retryDelay: TimeSpan.FromMilliseconds(10));
        }

        Assert.False(result.Success);
        Assert.Equal("old exe", Read(target, "Magpienest.exe"));
        Assert.Equal("old a", Read(target, "a.dll"));
        Assert.Equal("old z", Read(target, "z.dll"));
        Assert.False(File.Exists(Path.Combine(target, "b.dll")));
        Assert.Equal("stale", Read(target, @"plugins\p\stale.dll"));
    }

    [Fact]
    public void RefusesFoldersThatAreNotTwoCopiesOfMagpienest()
    {
        var target = Folder("installed", new() { ["Magpienest.exe"] = "old" });
        var inside = Folder(@"installed\sub", new() { ["Magpienest.exe"] = "new" });
        var empty = Folder("empty", new() { ["readme.txt"] = "" });

        Assert.False(UpdateApplier.Apply(inside, target, Path.Combine(_root, "b1"), _ => { }).Success);
        Assert.False(UpdateApplier.Apply(target, target, Path.Combine(_root, "b2"), _ => { }).Success);
        Assert.False(UpdateApplier.Apply(empty, target, Path.Combine(_root, "b3"), _ => { }).Success);
        Assert.False(UpdateApplier.Apply(target, empty, Path.Combine(_root, "b4"), _ => { }).Success);
        Assert.Equal("old", Read(target, "Magpienest.exe"));
    }

    private string Folder(string name, Dictionary<string, string> files)
    {
        var folder = Path.Combine(_root, name);
        foreach (var (relative, text) in files)
        {
            var path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Read(string folder, string relative) => File.ReadAllText(Path.Combine(folder, relative));
}
