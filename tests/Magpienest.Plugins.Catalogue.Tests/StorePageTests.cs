using System.Text.Json;
using Magpienest.Plugins.Catalogue;

namespace Magpienest.Plugins.Catalogue.Tests;

/// <summary>What the store page shows (phase 8 T8.2).</summary>
public sealed class StorePageTests
{
    private static readonly string LangDirectory = Path.Combine(FindRepoRoot(), "src", "plugins", "Magpienest.Plugins.Catalogue", "lang");

    [Theory]
    [InlineData(StoreBundleState.NotInstalled, StoreCardAction.Install)]
    [InlineData(StoreBundleState.UpdateAvailable, StoreCardAction.Update)]
    [InlineData(StoreBundleState.Installed, StoreCardAction.Installed)]
    [InlineData(StoreBundleState.Incompatible, StoreCardAction.Incompatible)]
    [InlineData(StoreBundleState.PendingRemoval, StoreCardAction.PendingRemoval)]
    internal void EachStateHasItsButton(StoreBundleState state, StoreCardAction expected) =>
        Assert.Equal(expected, StoreCards.ActionFor(state, installingThis: false));

    [Theory]
    [InlineData(StoreBundleState.NotInstalled)]
    [InlineData(StoreBundleState.UpdateAvailable)]
    internal void TheBundleBeingInstalledShowsCancel(StoreBundleState state) =>
        Assert.Equal(StoreCardAction.Cancel, StoreCards.ActionFor(state, installingThis: true));

    [Fact]
    public void OnlyInstallUpdateAndCancelCanBePressed()
    {
        Assert.True(StoreCards.CanAct(StoreCardAction.Install, anotherInstalling: false));
        Assert.True(StoreCards.CanAct(StoreCardAction.Update, anotherInstalling: false));
        Assert.True(StoreCards.CanAct(StoreCardAction.Cancel, anotherInstalling: false));
        Assert.False(StoreCards.CanAct(StoreCardAction.Installed, anotherInstalling: false));
        Assert.False(StoreCards.CanAct(StoreCardAction.Incompatible, anotherInstalling: false));
        Assert.False(StoreCards.CanAct(StoreCardAction.PendingRemoval, anotherInstalling: false));
    }

    [Fact]
    public void WhileAnotherBundleInstallsNothingElseCanStart()
    {
        Assert.False(StoreCards.CanAct(StoreCardAction.Install, anotherInstalling: true));
        Assert.False(StoreCards.CanAct(StoreCardAction.Update, anotherInstalling: true));
    }

    [Fact]
    public void StatusShowsLoadingFirstThenTheError()
    {
        Assert.Equal("Catalogue.Page.Loading", StoreCards.StatusKey(true, StoreError.Offline, true, 3));
        Assert.Equal("Catalogue.Error.Offline", StoreCards.StatusKey(false, StoreError.Offline, true, 3));
        Assert.Equal("Catalogue.Error.NotConfigured", StoreCards.StatusKey(false, StoreError.NotConfigured, false, 0));
    }

    [Fact]
    public void StatusIsEmptyWithBundlesAndSaysSoWithout()
    {
        Assert.Null(StoreCards.StatusKey(false, null, true, 2));
        Assert.Null(StoreCards.StatusKey(false, null, false, 0));
        Assert.Equal("Catalogue.Page.Empty", StoreCards.StatusKey(false, null, true, 0));
    }

    [Theory]
    [InlineData("en.json")]
    [InlineData("zh-TW.json")]
    public void EveryKeyThePageBuildsExists(string file)
    {
        var keys = Keys(file);
        foreach (var action in Enum.GetValues<StoreCardAction>())
        {
            Assert.Contains(StoreCards.ButtonKey(action), keys);
        }

        foreach (var error in Enum.GetValues<StoreError>())
        {
            Assert.Contains(StoreService.MessageKey(error), keys);
        }

        Assert.Contains("Catalogue.Page.Loading", keys);
        Assert.Contains("Catalogue.Page.Empty", keys);
    }

    [Fact]
    public void BothLanguageFilesHaveTheSameKeys() => Assert.Equal(Keys("en.json").Order(), Keys("zh-TW.json").Order());

    [Fact]
    public void RestartWaitsForTheOldProcessAndKeepsTheDataFolder()
    {
        var args = RestartCommand.AppArguments(1234, @"D:\my data", ["--verbose"]);
        Assert.Equal(["--wait-for-pid", "1234", "--data-dir", @"D:\my data", "--verbose"], args);
        var info = RestartCommand.Create(@"C:\app\Magpienest.exe", args);
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
