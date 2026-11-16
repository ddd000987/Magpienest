using System.Text.Json;
using Magpienest.Plugins.Settings;
using Magpienest.Sdk;

namespace Magpienest.Plugins.Settings.Tests;

public sealed class PluginInstallUiTests
{
    private static readonly string LangDirectory = Path.Combine(FindRepoRoot(), "src", "plugins", "Magpienest.Plugins.Settings", "lang");

    public static TheoryData<PluginInstallError> AllErrors()
    {
        var data = new TheoryData<PluginInstallError>();
        foreach (var value in Enum.GetValues<PluginInstallError>())
        {
            data.Add(value);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllErrors))]
    public void EveryErrorCodeMapsToAKeyThatExistsInBothLanguages(PluginInstallError code)
    {
        var key = PluginInstallMessages.KeyFor(code);
        Assert.Contains(key, Keys("en.json"));
        Assert.Contains(key, Keys("zh-TW.json"));
    }

    [Fact]
    public void EachRealErrorHasItsOwnMessageAndNoneUsesTheGenericOne()
    {
        var real = Enum.GetValues<PluginInstallError>().Where(e => e != PluginInstallError.None).ToList();
        var keys = real.Select(PluginInstallMessages.KeyFor).ToList();
        Assert.Equal(real.Count, keys.Distinct().Count());
        Assert.DoesNotContain(PluginInstallMessages.KeyFor(PluginInstallError.None), keys);
        Assert.Equal(PluginInstallMessages.KeyFor(PluginInstallError.None), PluginInstallMessages.KeyFor((PluginInstallError)999));
    }

    [Theory]
    [InlineData(true, true, "Settings.Plugins.RestartBar.Both")]
    [InlineData(true, false, "Settings.Plugins.RestartBar.Update")]
    [InlineData(false, true, "Settings.Plugins.RestartBar.Remove")]
    [InlineData(false, false, null)]
    public void RestartBarKeyFollowsPendingWork(bool update, bool removal, string? expected)
    {
        var key = PluginInstallMessages.RestartBarKey(update, removal);
        Assert.Equal(expected, key);
        if (key is not null)
        {
            Assert.Contains(key, Keys("en.json"));
            Assert.Contains(key, Keys("zh-TW.json"));
        }
    }

    [Fact]
    public void AppArgumentsWaitForTheOldProcessAndKeepTheDataFolderAndOtherArguments()
    {
        var args = RestartCommand.AppArguments(1234, @"D:\my data", ["--verbose", "--safe-mode"]);
        Assert.Equal(["--wait-for-pid", "1234", "--data-dir", @"D:\my data", "--verbose", "--safe-mode"], args);
    }

    [Fact]
    public void CreateStartsTheExeDirectlyWithEachArgumentSeparate()
    {
        var args = RestartCommand.AppArguments(77, @"D:\my data", ["--verbose"]);
        var info = RestartCommand.Create(@"C:\app\Magpienest.exe", args);
        Assert.Equal(@"C:\app\Magpienest.exe", info.FileName);
        Assert.False(info.UseShellExecute);
        Assert.Equal(args, info.ArgumentList);
    }

    private static HashSet<string> Keys(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(LangDirectory, file)));
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
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
}
