using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.Game.Tests;

/// <summary>A temporary folder tree of empty files.</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree() => Root = Directory.CreateTempSubdirectory("magpienest-game-").FullName;

    public string Root { get; }

    /// <summary>Creates a file (and its folders); returns its full path.</summary>
    public string File(string relative, int size = 0)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    public string Folder(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class GameFolderScannerTests
{
    private static GameFolderScanner Scanner(Func<string, string?>? product = null) => new(product ?? (_ => null));

    private static IReadOnlyList<GameCandidate> Analyze(TempTree tree, Func<string, string?>? product = null, GameLibraryIndex? index = null) =>
        Scanner(product).Analyze(tree.Root, index ?? GameLibraryIndex.Empty);

    // ----- candidates ---------------------------------------------------------------------------------------

    [Fact]
    public void EachSubFolderIsACandidateAndLooseFilesInTheRootAreIgnored()
    {
        using var tree = new TempTree();
        tree.File(@"Beta\beta.exe");
        tree.File(@"Alpha\alpha.exe");
        tree.File("loose.exe");

        var result = Analyze(tree);

        Assert.Equal(["Alpha", "Beta"], result.Select(c => c.Name));
        Assert.All(result, c => Assert.Equal(GameCandidateStatus.Ready, c.Status));
    }

    [Fact]
    public void ARootWithoutSubFoldersIsTheSingleCandidate()
    {
        using var tree = new TempTree();
        var exe = tree.File("game.exe");

        var result = Analyze(tree);

        var candidate = Assert.Single(result);
        Assert.Equal(tree.Root, candidate.Folder);
        Assert.Equal(exe, candidate.Default!.FullPath);
        Assert.Equal("game.exe", candidate.Default.RelativePath);
    }

    [Fact]
    public void ARootWithNothingInItGivesNoCandidates()
    {
        using var tree = new TempTree();
        tree.File("readme.txt");

        Assert.Empty(Analyze(tree));
    }

    [Fact]
    public void FoldersUpToFourLevelsDeepAreSearched()
    {
        using var tree = new TempTree();
        tree.File(@"Game\a\b\c\d\deep.exe");     // 4 levels below the candidate
        tree.File(@"Game\a\b\c\d\e\toodeep.exe"); // 5 levels

        var candidate = Assert.Single(Analyze(tree));

        Assert.Equal(["a\\b\\c\\d\\deep.exe"], candidate.Executables.Select(e => e.RelativePath));
    }

    [Fact]
    public void AtMostThreeHundredExecutablesAreCollected()
    {
        using var tree = new TempTree();
        for (var i = 0; i < 320; i++)
        {
            tree.File($@"Game\file{i:000}.exe");
        }

        var candidate = Assert.Single(Analyze(tree));

        Assert.Equal(GameFolderScanner.MaxFiles, candidate.Executables.Count);
    }

    [Fact]
    public void OnlyExeFilesCount()
    {
        using var tree = new TempTree();
        tree.File(@"Game\run.bat");
        tree.File(@"Game\data.dll");

        Assert.Equal(GameCandidateStatus.NoExecutable, Assert.Single(Analyze(tree)).Status);
    }

    // ----- masks --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("uninstall.exe")]
    [InlineData("Uninst.exe")]
    [InlineData("unins000.exe")]
    [InlineData("setup.exe")]
    [InlineData("GameSetup.exe")]
    [InlineData("config.exe")]
    [InlineData("DXSETUP.exe")]
    [InlineData("vc_redist.x64.exe")]
    [InlineData("vcredist_x86.exe")]
    [InlineData("redist.exe")]
    [InlineData("notification_helper.exe")]
    [InlineData("python.exe")]
    [InlineData("pythonw.exe")]
    [InlineData("zsync.exe")]
    [InlineData("zsyncmake.exe")]
    [InlineData("updater.exe")]
    [InlineData("CrashHandler.exe")]
    [InlineData("CrashReportClient.exe")]
    [InlineData("crashpad_handler.exe")]
    [InlineData("GameHelper.exe")]
    [InlineData("UE4PrereqSetup_x64.exe")]
    [InlineData("UEPrereqSetup_x64.exe")]
    [InlineData("QtWebEngineProcess.exe")]
    [InlineData("CefSharp.BrowserSubprocess.exe")]
    [InlineData("dotnetfx35.exe")]
    [InlineData("oalinst.exe")]
    [InlineData("installer.exe")]
    public void InstallersAndHelpersAreMasked(string fileName)
    {
        Assert.True(GameFolderScanner.IsMasked(fileName));
    }

    [Theory]
    [InlineData("game.exe")]
    [InlineData("MyGame-Win64-Shipping.exe")]
    [InlineData("launcher.exe")]
    [InlineData("my_python.exe")]       // the python masks are anchored
    [InlineData("pythonista.exe")]
    public void NormalNamesAreNotMasked(string fileName)
    {
        Assert.False(GameFolderScanner.IsMasked(fileName));
    }

    [Fact]
    public void MaskedFilesAreNotOffered()
    {
        using var tree = new TempTree();
        tree.File(@"Game\game.exe");
        tree.File(@"Game\unins000.exe");
        tree.File(@"Game\vcredist_x64.exe");

        var candidate = Assert.Single(Analyze(tree));

        Assert.Equal(["game.exe"], candidate.Executables.Select(e => e.RelativePath));
    }

    [Fact]
    public void AFolderWithOnlyMaskedFilesHasNoExecutable()
    {
        using var tree = new TempTree();
        tree.File(@"Game\setup.exe");
        tree.File(@"Game\crashpad_handler.exe");

        var candidate = Assert.Single(Analyze(tree));

        Assert.Equal(GameCandidateStatus.NoExecutable, candidate.Status);
        Assert.Null(candidate.Default);
        Assert.Empty(candidate.Executables);
    }

    // ----- scoring ------------------------------------------------------------------------------------------

    private static int ScoreOf(GameCandidate candidate, string relative) =>
        candidate.Executables.Single(e => e.RelativePath == relative).Score;

    [Fact]
    public void ANameEqualToTheFolderNameScoresOneHundred()
    {
        using var tree = new TempTree();
        tree.File(@"Cool Game\cool_game.exe");
        tree.File(@"Cool Game\launcher.exe");

        var candidate = Assert.Single(Analyze(tree));

        Assert.Equal(100, ScoreOf(candidate, "cool_game.exe"));
        Assert.Equal(0, ScoreOf(candidate, "launcher.exe"));
        Assert.Equal("cool_game.exe", candidate.Default!.RelativePath);
    }

    [Fact]
    public void OneNameContainingTheOtherScoresSixty()
    {
        using var tree = new TempTree();
        tree.File(@"Alpha Quest\AlphaQuestLauncher.exe");   // file contains folder
        tree.File(@"Alpha Quest\quest.exe");                // folder contains file (length 5)

        var candidate = Assert.Single(Analyze(tree));

        Assert.Equal(60, ScoreOf(candidate, "AlphaQuestLauncher.exe"));
        Assert.Equal(60, ScoreOf(candidate, "quest.exe"));
    }

    [Fact]
    public void ContainingNeedsAtLeastFourCharacters()
    {
        using var tree = new TempTree();
        tree.File(@"Abc Adventures\abc.exe");   // "abc" is contained but only 3 long

        var candidate = Assert.Single(Analyze(tree));

        Assert.Equal(0, ScoreOf(candidate, "abc.exe"));
    }

    [Fact]
    public void AProductNameEqualToTheFolderNameAddsThirty()
    {
        using var tree = new TempTree();
        var exe = tree.File(@"Space Trader\st64.exe");
        tree.File(@"Space Trader\other.exe");

        var candidate = Assert.Single(Analyze(tree, path => path == exe ? "Space-Trader" : null));

        Assert.Equal(30, ScoreOf(candidate, "st64.exe"));
        Assert.Equal(0, ScoreOf(candidate, "other.exe"));
        Assert.Equal("st64.exe", candidate.Default!.RelativePath);
    }

    [Fact]
    public void TheLargestFileAddsTwenty()
    {
        using var tree = new TempTree();
        tree.File(@"Game\small.exe", size: 10);
        tree.File(@"Game\big.exe", size: 1000);

        var candidate = Assert.Single(Analyze(tree));

        Assert.Equal(20, ScoreOf(candidate, "big.exe"));
        Assert.Equal(0, ScoreOf(candidate, "small.exe"));
    }

    [Fact]
    public void EmptyFilesAreNeverTheLargest()
    {
        using var tree = new TempTree();
        tree.File(@"Game\a.exe");
        tree.File(@"Game\b.exe");

        var candidate = Assert.Single(Analyze(tree));

        Assert.All(candidate.Executables, e => Assert.Equal(0, e.Score));
    }

    [Fact]
    public void EachFolderLevelCostsFifteen()
    {
        using var tree = new TempTree();
        tree.File(@"Game\a.exe");
        tree.File(@"Game\bin\b.exe");
        tree.File(@"Game\bin\x64\c.exe");

        var candidate = Assert.Single(Analyze(tree));

        Assert.Equal(0, ScoreOf(candidate, "a.exe"));
        Assert.Equal(-15, ScoreOf(candidate, @"bin\b.exe"));
        Assert.Equal(-30, ScoreOf(candidate, @"bin\x64\c.exe"));
    }

    [Theory]
    [InlineData("editor.exe")]
    [InlineData("DedicatedServer.exe")]
    [InlineData("benchmark.exe")]
    [InlineData("modtool.exe")]
    [InlineData("test.exe")]
    public void EditorsServersBenchmarksToolsAndTestsLoseFifty(string fileName)
    {
        using var tree = new TempTree();
        tree.File(@"Game\" + fileName);

        var candidate = Assert.Single(Analyze(tree));

        Assert.Equal(-50, ScoreOf(candidate, fileName));
    }

    [Fact]
    public void ThePointsAreAddedUp()
    {
        using var tree = new TempTree();
        // folder "Dungeon Crawler": name match 100 + largest 20 - one level 15 = 105
        tree.File(@"Dungeon Crawler\bin\DungeonCrawler.exe", size: 500);
        tree.File(@"Dungeon Crawler\crawler_editor.exe", size: 100);   // no name match, editor -50

        var candidate = Assert.Single(Analyze(tree));

        Assert.Equal(105, ScoreOf(candidate, @"bin\DungeonCrawler.exe"));
        Assert.Equal(-50, ScoreOf(candidate, "crawler_editor.exe"));
        Assert.Equal(@"bin\DungeonCrawler.exe", candidate.Default!.RelativePath);
    }

    // ----- ties and order -----------------------------------------------------------------------------------

    [Fact]
    public void EqualScoresAreOrderedAlphabetically()
    {
        using var tree = new TempTree();
        tree.File(@"Game\b.exe");
        tree.File(@"Game\a.exe");
        tree.File(@"Game\c.exe");

        var candidate = Assert.Single(Analyze(tree));

        Assert.Equal(["a.exe", "b.exe", "c.exe"], candidate.Executables.Select(e => e.RelativePath));
    }

    [Fact]
    public void EqualScoresPreferTheShallowerPath()
    {
        using var tree = new TempTree();
        var deep = tree.File(@"Game\x\y\aaa.exe");   // -30, +30 from the product name = 0; first alphabetically
        tree.File(@"Game\zzz.exe");                  // 0

        var candidate = Assert.Single(Analyze(tree, path => path == deep ? "game" : null));

        Assert.Equal(0, ScoreOf(candidate, @"x\y\aaa.exe"));
        Assert.Equal(["zzz.exe", @"x\y\aaa.exe"], candidate.Executables.Select(e => e.RelativePath));
    }

    // ----- already in the library ---------------------------------------------------------------------------

    [Fact]
    public void AGameWithTheSameProgramIsAlreadyInTheLibrary()
    {
        using var tree = new TempTree();
        var exe = tree.File(@"Game\game.exe");
        tree.File(@"Other\other.exe");
        var index = new GameLibraryIndex([exe.ToUpperInvariant()], []);

        var result = Analyze(tree, index: index);

        Assert.Equal(GameCandidateStatus.AlreadyInLibrary, result.Single(c => c.Name == "Game").Status);
        Assert.Equal(GameCandidateStatus.Ready, result.Single(c => c.Name == "Other").Status);
    }

    [Fact]
    public void AGameWithTheSameInstallFolderIsAlreadyInTheLibrary()
    {
        using var tree = new TempTree();
        tree.File(@"Game\game.exe");
        var folder = tree.Folder("Game");
        var index = new GameLibraryIndex([@"C:\elsewhere\launcher.exe"], [folder + Path.DirectorySeparatorChar]);

        var candidate = Assert.Single(Analyze(tree, index: index));

        Assert.Equal(GameCandidateStatus.AlreadyInLibrary, candidate.Status);
    }

    [Fact]
    public void AFolderWithoutAProgramStaysNoExecutableEvenIfItsFolderIsKnown()
    {
        using var tree = new TempTree();
        var folder = tree.Folder("Game");
        var index = new GameLibraryIndex([], [folder]);

        Assert.Equal(GameCandidateStatus.NoExecutable, Assert.Single(Analyze(tree, index: index)).Status);
    }

    [Fact]
    public void TheIndexTakesTheInstallFolderOfAGameFromItsExtraOrFromTheProgramsFolder()
    {
        using var tree = new TempTree();
        var withExtra = tree.File(@"A\bin\a.exe");
        var withoutExtra = tree.File(@"B\b.exe");
        var items = new[]
        {
            GameItem(withExtra, LibraryItemExtensions.SerializeExtra(new GameExtra { InstallDir = tree.Folder("A") })),
            GameItem(withoutExtra, "{}"),
        };

        var result = Analyze(tree, index: GameLibraryIndex.From(items));

        Assert.All(result, c => Assert.Equal(GameCandidateStatus.AlreadyInLibrary, c.Status));
    }

    private static LibraryItem GameItem(string exe, string extra) => new()
    {
        Kind = "game",
        Path = exe,
        Directory = Path.GetDirectoryName(exe)!,
        Title = "Game",
        Extra = extra,
    };

    [Theory]
    [InlineData("Cool Game!", "coolgame")]
    [InlineData("A_b-C 9", "abc9")]
    [InlineData("---", "")]
    public void NormalizeKeepsLowerCaseLettersAndDigits(string text, string expected)
    {
        Assert.Equal(expected, GameFolderScanner.Normalize(text));
    }
}
