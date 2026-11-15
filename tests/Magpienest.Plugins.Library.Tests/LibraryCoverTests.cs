using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.Library.Tests;

/// <summary>03-data-model §7: covers live in the media folder; those are the only files the library deletes.</summary>
public sealed class LibraryCoverTests : IDisposable
{
    private const string Kind = "illustration";
    private readonly TestLibrary _lib = new();

    private LibraryService Service => _lib.Service;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _lib.Dispose();

    private string MakeImage(string name, byte fill)
    {
        var path = Path.Combine(_lib.NewFolder("covers"), name);
        File.WriteAllBytes(path, [fill, fill, fill, fill]);
        return path;
    }

    [Fact]
    public async Task SetCoverCopiesTheFileIntoTheMediaFolder()
    {
        var item = await _lib.AddAsync(Kind, @"C:\lib\a\one.png");
        var source = MakeImage("pic.PNG", 7);

        await Service.UpdateAsync(item.Id, new ItemPatch { SetCoverFromFile = source });

        var updated = (await Service.GetAsync(item.Id))!;
        var expected = Path.Combine(_lib.MediaDirectory, item.Id.ToString(), "cover.PNG");
        Assert.Equal(expected, updated.CoverPath);
        Assert.Equal(await File.ReadAllBytesAsync(source, Ct), await File.ReadAllBytesAsync(expected, Ct));
        Assert.True(File.Exists(source)); // the user's file is untouched
        Assert.Equal(Path.Combine(item.Id.ToString(), "cover.PNG"), await _lib.ScalarAsync<string>("SELECT cover FROM item WHERE id = $id", ("$id", item.Id)));
    }

    [Fact]
    public async Task ReplacingTheCoverRemovesTheOldFile()
    {
        var item = await _lib.AddAsync(Kind, @"C:\lib\a\one.png");
        await Service.UpdateAsync(item.Id, new ItemPatch { SetCoverFromFile = MakeImage("one.png", 1) });
        var folder = Path.Combine(_lib.MediaDirectory, item.Id.ToString());
        Assert.True(File.Exists(Path.Combine(folder, "cover.png")));

        await Service.UpdateAsync(item.Id, new ItemPatch { SetCoverFromFile = MakeImage("two.jpg", 2) });
        Assert.False(File.Exists(Path.Combine(folder, "cover.png")));
        Assert.Equal([Path.Combine(folder, "cover.jpg")], Directory.GetFiles(folder));
        Assert.Equal([2, 2, 2, 2], await File.ReadAllBytesAsync(Path.Combine(folder, "cover.jpg"), Ct));

        await Service.UpdateAsync(item.Id, new ItemPatch { SetCoverFromFile = MakeImage("three.jpg", 3) }); // same extension: overwritten
        Assert.Equal([3, 3, 3, 3], await File.ReadAllBytesAsync(Path.Combine(folder, "cover.jpg"), Ct));
    }

    [Fact]
    public async Task ClearCoverDeletesTheCopyOnly()
    {
        var item = await _lib.AddAsync(Kind, @"C:\lib\a\one.png");
        var source = MakeImage("pic.png", 9);
        await Service.UpdateAsync(item.Id, new ItemPatch { SetCoverFromFile = source });

        await Service.UpdateAsync(item.Id, new ItemPatch { ClearCover = true });

        Assert.Null((await Service.GetAsync(item.Id))!.CoverPath);
        Assert.False(Directory.Exists(Path.Combine(_lib.MediaDirectory, item.Id.ToString())));
        Assert.True(File.Exists(source));
        await Service.UpdateAsync(item.Id, new ItemPatch { ClearCover = true }); // nothing to clear: harmless
    }

    [Fact]
    public async Task RemovingItemsDeletesTheirMediaFolders()
    {
        var one = await _lib.AddAsync(Kind, @"C:\lib\a\one.png");
        var two = await _lib.AddAsync(Kind, @"C:\lib\a\two.png");
        await Service.UpdateAsync(one.Id, new ItemPatch { SetCoverFromFile = MakeImage("a.png", 1) });
        await Service.UpdateAsync(two.Id, new ItemPatch { SetCoverFromFile = MakeImage("b.png", 2) });

        await Service.RemoveAsync([one.Id]);

        Assert.False(Directory.Exists(Path.Combine(_lib.MediaDirectory, one.Id.ToString())));
        Assert.True(Directory.Exists(Path.Combine(_lib.MediaDirectory, two.Id.ToString())));
    }

    [Fact]
    public async Task RemovingASourceDeletesTheMediaFoldersOfItsItems()
    {
        var source = await Service.AddSourceAsync(Kind, @"C:\lib");
        var inside = await _lib.AddAsync(Kind, @"C:\lib\a.png");
        var manual = await _lib.AddAsync(Kind, @"D:\elsewhere\b.png");
        await _lib.ExecAsync("UPDATE item SET source_id = $s WHERE id = $id", ("$s", source.Id), ("$id", inside.Id));
        await Service.UpdateAsync(inside.Id, new ItemPatch { SetCoverFromFile = MakeImage("a.png", 1) });
        await Service.UpdateAsync(manual.Id, new ItemPatch { SetCoverFromFile = MakeImage("b.png", 2) });

        await Service.RemoveSourceAsync(source.Id);

        Assert.False(Directory.Exists(Path.Combine(_lib.MediaDirectory, inside.Id.ToString())));
        Assert.True(Directory.Exists(Path.Combine(_lib.MediaDirectory, manual.Id.ToString())));
    }

    [Fact]
    public async Task UsingTheStoredCoverAsTheSourceDoesNotFail()
    {
        var item = await _lib.AddAsync(Kind, @"C:\lib\a\one.png");
        await Service.UpdateAsync(item.Id, new ItemPatch { SetCoverFromFile = MakeImage("a.png", 5) });
        var stored = (await Service.GetAsync(item.Id))!.CoverPath!;

        await Service.UpdateAsync(item.Id, new ItemPatch { SetCoverFromFile = stored });

        Assert.Equal([5, 5, 5, 5], await File.ReadAllBytesAsync(stored, Ct));
    }
}
