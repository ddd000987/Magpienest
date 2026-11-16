using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Magpienest.Plugins.Game.Tests;

public sealed class GameTests
{
    private static string Fake(string key, params object?[] args) =>
        args.Length == 0 ? key : key + ":" + string.Join(",", args);

    [Theory]
    [InlineData(0, 0, "Game.PlayTime.Never")]
    [InlineData(0, 3, "Game.PlayTime.LessThanMinute")]     // started, but no time recorded
    [InlineData(1, 0, "Game.PlayTime.LessThanMinute")]
    [InlineData(59, 1, "Game.PlayTime.LessThanMinute")]
    [InlineData(60, 1, "Game.PlayTime.Minutes:1")]
    [InlineData(59 * 60 + 59, 1, "Game.PlayTime.Minutes:59")]
    [InlineData(3600, 1, "Game.PlayTime.HoursMinutes:1,0")]
    [InlineData(3 * 3600 + 20 * 60 + 30, 1, "Game.PlayTime.HoursMinutes:3,20")]
    [InlineData(100 * 3600, 1, "Game.PlayTime.HoursMinutes:100,0")]
    [InlineData(-5, 0, "Game.PlayTime.Never")]
    public void PlayTimeIsFormattedFromSeconds(long seconds, int openCount, string expected)
    {
        Assert.Equal(expected, GameFormat.PlayTime(seconds, openCount, Fake));
    }

    [Fact]
    public void TheTitleIsTheProductNameWhenThereIsOne()
    {
        Assert.Equal("My Game", GameFormat.DefaultTitle(@"C:\Games\Folder\game.exe", _ => "  My Game "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheTitleIsTheFolderNameWhenTheProductNameIsBlank(string? productName)
    {
        Assert.Equal("Folder Name", GameFormat.DefaultTitle(@"C:\Games\Folder Name\game.exe", _ => productName));
    }

    [Fact]
    public void AFileWithoutVersionInformationGetsTheFolderName()
    {
        using var tree = new TempTree();
        var exe = tree.File(@"Dark Castle\dc.exe");

        Assert.Equal("Dark Castle", GameFormat.DefaultTitle(exe));
    }

    [Fact]
    public void AProgramAtTheRootOfADriveGetsTheFileName()
    {
        Assert.Equal("game", GameFormat.DefaultTitle(@"C:\game.exe", _ => null));
    }

    [Fact]
    public void InstallAndWorkingFoldersDefaultToTheProgramsFolder()
    {
        var item = new Magpienest.Sdk.Library.LibraryItem
        {
            Kind = "game",
            Path = @"C:\Games\A\a.exe",
            Directory = @"C:\Games\A",
            Title = "A",
        };

        Assert.Equal(@"C:\Games\A", GameExtra.EffectiveInstallDir(item));
        Assert.Equal(@"C:\Games\A", GameExtra.EffectiveWorkingDir(item));

        var custom = item with
        {
            Extra = Magpienest.Sdk.Library.LibraryItemExtensions.SerializeExtra(
                new GameExtra { InstallDir = @"D:\Install", WorkingDir = @"D:\Work", Arguments = "-windowed" }),
        };
        Assert.Equal(@"D:\Install", GameExtra.EffectiveInstallDir(custom));
        Assert.Equal(@"D:\Work", GameExtra.EffectiveWorkingDir(custom));
        Assert.Equal("-windowed", Magpienest.Sdk.Library.LibraryItemExtensions.GetExtra<GameExtra>(custom).Arguments);
    }

    // ----- shortcuts ----------------------------------------------------------------------------------------

    [Fact]
    public void AShortcutIsResolvedToItsTargetArgumentsAndWorkingFolder()
    {
        using var tree = new TempTree();
        var target = tree.File(@"Game\game.exe");
        var work = tree.Folder("Work");
        var lnk = Path.Combine(tree.Root, "Game.lnk");
        CreateShortcut(lnk, target, "-fullscreen -level 2", work);

        var resolved = ShortcutResolver.Resolve(lnk);

        Assert.NotNull(resolved);
        Assert.Equal(target, resolved.TargetPath, ignoreCase: true);
        Assert.Equal("-fullscreen -level 2", resolved.Arguments);
        Assert.Equal(work, resolved.WorkingDirectory, ignoreCase: true);
    }

    [Fact]
    public void AMissingOrBrokenShortcutResolvesToNull()
    {
        using var tree = new TempTree();
        Assert.Null(ShortcutResolver.Resolve(Path.Combine(tree.Root, "missing.lnk")));

        var broken = tree.File("broken.lnk");
        File.WriteAllText(broken, "this is not a shortcut");
        Assert.Null(ShortcutResolver.Resolve(broken));
    }

    [Theory]
    [InlineData(@"C:\a\b.lnk", true)]
    [InlineData(@"C:\a\b.LNK", true)]
    [InlineData(@"C:\a\b.exe", false)]
    public void ShortcutsAreRecognisedByTheirExtension(string path, bool expected)
    {
        Assert.Equal(expected, ShortcutResolver.IsShortcut(path));
    }

    private static void CreateShortcut(string lnk, string target, string arguments, string workingDirectory)
    {
        var link = new ShellLink();
        try
        {
            var shell = (IShellLinkW)link;
            shell.SetPath(target);
            shell.SetArguments(arguments);
            shell.SetWorkingDirectory(workingDirectory);
            ((IPersistFile)link).Save(lnk, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    // ----- cover card ---------------------------------------------------------------------------------------

    [Fact]
    public void TheCardPutsTheIconInTheMiddleOnAGreyBackground()
    {
        var pixels = new byte[32 * 32 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 2] = 0xFF;   // red
            pixels[i + 3] = 0xFF;
        }

        var icon = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);

        var card = GameCard.Compose(icon, 384);

        Assert.Equal(GameCard.Width, card.PixelWidth);
        Assert.Equal(GameCard.Height, card.PixelHeight);
        Assert.True(card.IsFrozen);
        var middle = new byte[4];
        card.CopyPixels(new System.Windows.Int32Rect(GameCard.Width / 2, GameCard.Height / 2, 1, 1), middle, 4, 0);
        Assert.Equal([0x00, 0x00, 0xFF], middle[..3]);
        var corner = new byte[4];
        card.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), corner, 4, 0);
        Assert.Equal(corner[0], corner[1]);
        Assert.Equal(corner[1], corner[2]);
        Assert.NotEqual(0xFF, corner[0]);
        Assert.NotEqual(0, corner[0]);
    }

    [Fact]
    public void ATransparentIconPixelShowsTheBackground()
    {
        var pixels = new byte[4 * 4 * 4];   // fully transparent
        var icon = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 4 * 4);

        var card = GameCard.Compose(icon, 384);

        var middle = new byte[4];
        card.CopyPixels(new System.Windows.Int32Rect(GameCard.Width / 2, GameCard.Height / 2, 1, 1), middle, 4, 0);
        var corner = new byte[4];
        card.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), corner, 4, 0);
        Assert.Equal(corner, middle);
    }
}
