using System.Diagnostics;
using Magpienest.Sdk.Library;
using Microsoft.Data.Sqlite;

namespace Magpienest.Plugins.Library.Tests;

/// <summary>T1.1: schema, items, tags, queries, sources.</summary>
public sealed class LibraryDatabaseTests : IDisposable
{
    private const string Kind = "illustration";
    private readonly TestLibrary _lib = new();

    private LibraryService Service => _lib.Service;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _lib.Dispose();

    // ----- migrations and connection rules ---------------------------------------------------------------------

    [Fact]
    public void MigrationCreatesSchemaAndDoesNotRunAgainOnReopen()
    {
        var path = Path.Combine(_lib.Root, "migration", "test.db");
        using (var db = new LibraryDb(path))
        {
            Assert.Equal(0, db.GetVersion());
            Assert.Equal(LibraryDb.Migrations.Count, db.Migrate());
            Assert.Equal(LibraryDb.Migrations.Max(m => m.Version), db.GetVersion());

            var objects = db.Read(connection =>
            {
                using var command = connection.Command("SELECT name FROM sqlite_master WHERE type IN ('table', 'index')");
                using var reader = command.ExecuteReader();
                var names = new List<string>();
                while (reader.Read())
                {
                    names.Add(reader.GetString(0));
                }

                return names;
            });
            foreach (var expected in new[] { "source", "item", "tag", "item_tag", "ix_item_kind_dir", "ix_item_kind_added", "ix_item_source", "ix_item_tag_tag" })
            {
                Assert.Contains(expected, objects);
            }

            db.Write(connection => connection.Command("INSERT INTO tag (name) VALUES ('keep me')").Execute());
        }

        using (var reopened = new LibraryDb(path))
        {
            Assert.Equal(0, reopened.Migrate());
            Assert.Equal(1, reopened.Read(connection => Convert.ToInt32(connection.Command("SELECT COUNT(*) FROM tag").Scalar())));
        }
    }

    [Fact]
    public void EveryConnectionGetsThePragmas()
    {
        using var db = new LibraryDb(Path.Combine(_lib.Root, "pragmas", "test.db"));
        db.Migrate();
        for (var i = 0; i < 3; i++)
        {
            using var connection = db.OpenConnection();
            Assert.Equal(1L, connection.Command("PRAGMA foreign_keys").Scalar());
            Assert.Equal("wal", connection.Command("PRAGMA journal_mode").Scalar());
            Assert.Equal(1L, connection.Command("PRAGMA synchronous").Scalar());
            Assert.Equal(5000L, connection.Command("PRAGMA busy_timeout").Scalar());
        }
    }

    // ----- items -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddGetUpdateRemoveItem()
    {
        var added = await Service.AddAsync(new NewItem { Kind = Kind, Path = @"C:\lib\a\one.png", Title = "One", Extra = """{"x":1}""" });
        Assert.True(added.Id > 0);
        Assert.Equal(Kind, added.Kind);
        Assert.Equal(@"C:\lib\a\one.png", added.Path);
        Assert.Equal(@"C:\lib\a", added.Directory);
        Assert.Equal("One", added.Title);
        Assert.Null(added.SourceId);
        Assert.Equal("""{"x":1}""", added.Extra);
        Assert.False(added.IsMissing);
        Assert.True(added.AddedAt > DateTimeOffset.UtcNow.AddMinutes(-1));

        var fetched = await Service.GetAsync(added.Id);
        Assert.Equal(added, fetched);
        Assert.Null(await Service.GetAsync(added.Id + 999));

        await Service.RemoveAsync([added.Id]);
        Assert.Null(await Service.GetAsync(added.Id));
        await Service.RemoveAsync([added.Id]); // removing again is harmless
    }

    [Fact]
    public async Task AddNormalisesPath()
    {
        var item = await Service.AddAsync(new NewItem { Kind = Kind, Path = @"C:\lib\a\..\b\two.png", Title = "Two" });
        Assert.Equal(@"C:\lib\b\two.png", item.Path);
        Assert.Equal(@"C:\lib\b", item.Directory);
    }

    [Fact]
    public async Task EveryPatchMemberIsApplied()
    {
        var item = await _lib.AddAsync(Kind, @"C:\lib\a\one.png");
        await Service.UpdateAsync(item.Id, new ItemPatch
        {
            Title = "New title",
            Rating = 4,
            IsFavorite = true,
            Status = ItemStatus.OnHold,
            ProgressPosition = 7,
            ProgressTotal = 9,
            AddTimeSpentSeconds = 30,
            Notes = "some notes",
            Extra = """{"direction":"rtl"}""",
        });
        await Service.UpdateAsync(item.Id, new ItemPatch { AddTimeSpentSeconds = 12 });

        var updated = (await Service.GetAsync(item.Id))!;
        Assert.Equal("New title", updated.Title);
        Assert.Equal(4, updated.Rating);
        Assert.True(updated.IsFavorite);
        Assert.Equal(ItemStatus.OnHold, updated.Status);
        Assert.Equal(7, updated.ProgressPosition);
        Assert.Equal(9, updated.ProgressTotal);
        Assert.Equal(42, updated.TimeSpentSeconds);
        Assert.Equal("some notes", updated.Notes);
        Assert.Equal("""{"direction":"rtl"}""", updated.Extra);
        Assert.Equal(@"C:\lib\a\one.png", updated.Path); // untouched members stay

        await Service.UpdateAsync(item.Id, new ItemPatch { IsFavorite = false, Rating = 0, Status = ItemStatus.None });
        updated = (await Service.GetAsync(item.Id))!;
        Assert.False(updated.IsFavorite);
        Assert.Equal(0, updated.Rating);
        Assert.Equal(ItemStatus.None, updated.Status);
        Assert.Equal("New title", updated.Title);
    }

    [Fact]
    public async Task MarkOpenedSetsTimeAndCounts()
    {
        var item = await _lib.AddAsync(Kind, @"C:\lib\a\one.png");
        Assert.Null(item.LastOpenedAt);
        Assert.Equal(0, item.OpenCount);

        await Service.UpdateAsync(item.Id, new ItemPatch { MarkOpened = true });
        var first = (await Service.GetAsync(item.Id))!;
        Assert.NotNull(first.LastOpenedAt);
        Assert.Equal(1, first.OpenCount);

        await Service.UpdateAsync(item.Id, new ItemPatch { MarkOpened = true });
        var second = (await Service.GetAsync(item.Id))!;
        Assert.Equal(2, second.OpenCount);
        Assert.True(second.LastOpenedAt >= first.LastOpenedAt);
    }

    [Fact]
    public async Task PathCanBePatchedOnlyForItemsWithoutSource()
    {
        var manual = await _lib.AddAsync(Kind, @"C:\lib\a\one.png");
        await Service.UpdateAsync(manual.Id, new ItemPatch { Path = @"D:\other\renamed.png" });
        var moved = (await Service.GetAsync(manual.Id))!;
        Assert.Equal(@"D:\other\renamed.png", moved.Path);
        Assert.Equal(@"D:\other", moved.Directory);

        var other = await _lib.AddAsync(Kind, @"C:\lib\a\two.png");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.UpdateAsync(other.Id, new ItemPatch { Path = @"D:\other\RENAMED.png" }));

        var source = await Service.AddSourceAsync(Kind, @"C:\sourced");
        await _lib.ExecAsync("UPDATE item SET source_id = $s WHERE id = $id", ("$s", source.Id), ("$id", other.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.UpdateAsync(other.Id, new ItemPatch { Path = @"C:\lib\a\three.png" }));
    }

    [Fact]
    public async Task InvalidPatchValuesAreRejected()
    {
        var item = await _lib.AddAsync(Kind, @"C:\lib\a\one.png");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service.UpdateAsync(item.Id, new ItemPatch { Rating = 6 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service.UpdateAsync(item.Id, new ItemPatch { Rating = -1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => Service.UpdateAsync(item.Id, new ItemPatch { Title = "  " }));
        await Service.UpdateAsync(item.Id + 12345, new ItemPatch { Rating = 3 }); // unknown id: no-op
    }

    [Fact]
    public async Task KindAndPathAreUniqueIgnoringCase()
    {
        await _lib.AddAsync(Kind, @"C:\lib\a\One.png");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _lib.AddAsync(Kind, @"c:\LIB\a\one.PNG"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _lib.AddAsync(Kind, @"C:\lib\a\One.png\", "x"));

        var otherKind = await _lib.AddAsync("comic", @"C:\lib\a\One.png");
        Assert.Equal("comic", otherKind.Kind);
    }

    [Fact]
    public async Task ItemChangesAreAnnounced()
    {
        var item = await _lib.AddAsync(Kind, @"C:\lib\a\one.png");
        await Service.UpdateAsync(item.Id, new ItemPatch { Rating = 2 });
        await Service.SetTagsAsync(item.Id, ["x"]);
        await Service.UpdateAsync(item.Id, new ItemPatch()); // nothing to change: no event
        await Service.RemoveAsync([item.Id]);

        Assert.Equal(
            [
                new LibraryChanged(Kind, LibraryChangeType.Added, [item.Id]),
                new LibraryChanged(Kind, LibraryChangeType.Updated, [item.Id]),
                new LibraryChanged(Kind, LibraryChangeType.Updated, [item.Id]),
                new LibraryChanged(Kind, LibraryChangeType.Removed, [item.Id]),
            ],
            _lib.Events,
            new ChangedComparer());
    }

    // ----- tags ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TagsAreSetReplacedAndNormalised()
    {
        var item = await _lib.AddAsync(Kind, @"C:\lib\a\one.png");
        await Service.SetTagsAsync(item.Id, ["  cat ", "Blue   sky", "artist:foo", "CAT", "", "   "]);
        Assert.Equal(["artist:foo", "Blue sky", "cat"], (await Service.GetAsync(item.Id))!.Tags);

        await Service.SetTagsAsync(item.Id, ["dog"]);
        Assert.Equal(["dog"], (await Service.GetAsync(item.Id))!.Tags);

        await Service.SetTagsAsync(item.Id, []);
        Assert.Empty((await Service.GetAsync(item.Id))!.Tags);

        await Service.SetTagsAsync(item.Id, [new string('x', 150)]);
        Assert.Equal(100, Assert.Single((await Service.GetAsync(item.Id))!.Tags).Length);
    }

    [Fact]
    public async Task TagNamesAreCaseInsensitivelyUniqueAcrossItems()
    {
        var one = await _lib.AddAsync(Kind, @"C:\lib\a\one.png");
        var two = await _lib.AddAsync(Kind, @"C:\lib\a\two.png");
        await Service.SetTagsAsync(one.Id, ["Cat"]);
        await Service.SetTagsAsync(two.Id, ["cat", "CAT"]);

        Assert.Equal(1, await _lib.ScalarAsync<int>("SELECT COUNT(*) FROM tag"));
        var counts = await Service.GetTagsAsync(Kind);
        Assert.Equal([new TagCount("Cat", 2)], counts);
        Assert.Equal(["Cat"], (await Service.GetAsync(two.Id))!.Tags);
    }

    [Fact]
    public async Task OrphanTagsAreDeleted()
    {
        var one = await _lib.AddAsync(Kind, @"C:\lib\a\one.png");
        var two = await _lib.AddAsync(Kind, @"C:\lib\a\two.png");
        await Service.SetTagsAsync(one.Id, ["shared", "only-one"]);
        await Service.SetTagsAsync(two.Id, ["shared"]);
        Assert.Equal(2, await _lib.ScalarAsync<int>("SELECT COUNT(*) FROM tag"));

        await Service.SetTagsAsync(one.Id, ["shared"]);
        Assert.Equal(1, await _lib.ScalarAsync<int>("SELECT COUNT(*) FROM tag"));

        await Service.RemoveAsync([one.Id]);
        Assert.Equal(1, await _lib.ScalarAsync<int>("SELECT COUNT(*) FROM tag"));

        await Service.SetTagsAsync(two.Id, []);
        Assert.Equal(0, await _lib.ScalarAsync<int>("SELECT COUNT(*) FROM tag"));
        Assert.Equal(0, await _lib.ScalarAsync<int>("SELECT COUNT(*) FROM item_tag"));
    }

    [Fact]
    public async Task GetTagsCountsPerKind()
    {
        var one = await _lib.AddAsync(Kind, @"C:\lib\a\one.png");
        var two = await _lib.AddAsync(Kind, @"C:\lib\a\two.png");
        var comic = await _lib.AddAsync("comic", @"C:\lib\a\book");
        await Service.SetTagsAsync(one.Id, ["cat", "blue"]);
        await Service.SetTagsAsync(two.Id, ["cat"]);
        await Service.SetTagsAsync(comic.Id, ["cat", "manga"]);

        Assert.Equal([new TagCount("blue", 1), new TagCount("cat", 2)], await Service.GetTagsAsync(Kind));
        Assert.Equal([new TagCount("cat", 1), new TagCount("manga", 1)], await Service.GetTagsAsync("comic"));
        Assert.Empty(await Service.GetTagsAsync("video"));
    }

    // ----- queries ---------------------------------------------------------------------------------------------

    private sealed record Seed(LibraryItem A, LibraryItem B, LibraryItem C, LibraryItem D, LibraryItem Comic, LibrarySource Source);

    /// <summary>
    /// A: lib\a\one.png "Alpha" 5 stars, favourite, Completed, tags cat+Blue.
    /// B: lib\a\sub\two.png "Beta" 3 stars, tag cat. C: lib\b\three.png "Gamma_100%" 1 star, InProgress.
    /// D: lib\b\four.png "Delta", missing. Comic: lib\a\book "Alpha".
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        var source = await Service.AddSourceAsync(Kind, @"C:\lib\b");
        var a = await _lib.AddAsync(Kind, @"C:\lib\a\one.png", "Alpha");
        var b = await _lib.AddAsync(Kind, @"C:\lib\a\sub\two.png", "Beta");
        var c = await _lib.AddAsync(Kind, @"C:\lib\b\three.png", "Gamma_100%");
        var d = await _lib.AddAsync(Kind, @"C:\lib\b\four.png", "Delta");
        var comic = await _lib.AddAsync("comic", @"C:\lib\a\book", "Alpha");

        await Service.UpdateAsync(a.Id, new ItemPatch { Rating = 5, IsFavorite = true, Status = ItemStatus.Completed });
        await Service.UpdateAsync(b.Id, new ItemPatch { Rating = 3 });
        await Service.UpdateAsync(c.Id, new ItemPatch { Rating = 1, Status = ItemStatus.InProgress });
        await Service.SetTagsAsync(a.Id, ["cat", "Blue"]);
        await Service.SetTagsAsync(b.Id, ["cat"]);
        await _lib.ExecAsync("UPDATE item SET missing = 1 WHERE id = $id", ("$id", d.Id));
        await _lib.ExecAsync("UPDATE item SET source_id = $s WHERE id IN ($c, $d)", ("$s", source.Id), ("$c", c.Id), ("$d", d.Id));
        return new Seed(a, b, c, d, comic, source);
    }

    private async Task<string[]> TitlesAsync(ItemQuery query) =>
        [.. (await Service.QueryAsync(query, Ct)).Select(i => i.Title)];

    [Fact]
    public async Task QueryKindAndMissing()
    {
        await SeedAsync();
        Assert.Equal(["Alpha", "Beta", "Gamma_100%"], await TitlesAsync(new ItemQuery { Kind = Kind }));
        Assert.Equal(["Alpha", "Beta", "Delta", "Gamma_100%"], await TitlesAsync(new ItemQuery { Kind = Kind, IncludeMissing = true }));
        Assert.Equal(["Alpha"], await TitlesAsync(new ItemQuery { Kind = "comic" }));
        Assert.Empty(await TitlesAsync(new ItemQuery { Kind = "video" }));
    }

    [Fact]
    public async Task QueryText()
    {
        await SeedAsync();
        Assert.Equal(["Alpha"], await TitlesAsync(new ItemQuery { Kind = Kind, Text = "ALP" }));
        Assert.Equal(["Alpha", "Beta"], await TitlesAsync(new ItemQuery { Kind = Kind, Text = "cat" })); // tag match
        Assert.Equal(["Alpha"], await TitlesAsync(new ItemQuery { Kind = Kind, Text = "blu" }));
        Assert.Equal(["Gamma_100%"], await TitlesAsync(new ItemQuery { Kind = Kind, Text = "%" }));
        Assert.Equal(["Gamma_100%"], await TitlesAsync(new ItemQuery { Kind = Kind, Text = "_" }));
        Assert.Equal(["Gamma_100%"], await TitlesAsync(new ItemQuery { Kind = Kind, Text = "a_100%" }));
        Assert.Empty(await TitlesAsync(new ItemQuery { Kind = Kind, Text = "\\" }));
        Assert.Equal(["Alpha", "Beta", "Gamma_100%"], await TitlesAsync(new ItemQuery { Kind = Kind, Text = "" }));
    }

    [Fact]
    public async Task QueryDirectory()
    {
        await SeedAsync();
        Assert.Equal(["Alpha", "Beta"], await TitlesAsync(new ItemQuery { Kind = Kind, Directory = @"C:\lib\a" }));
        Assert.Equal(["Alpha", "Beta"], await TitlesAsync(new ItemQuery { Kind = Kind, Directory = @"c:\LIB\a\" }));
        Assert.Equal(["Alpha"], await TitlesAsync(new ItemQuery { Kind = Kind, Directory = @"C:\lib\a", IncludeSubdirectories = false }));
        Assert.Equal(["Beta"], await TitlesAsync(new ItemQuery { Kind = Kind, Directory = @"C:\lib\a\sub" }));
        Assert.Equal(["Alpha", "Beta", "Gamma_100%"], await TitlesAsync(new ItemQuery { Kind = Kind, Directory = @"C:\lib" }));
        Assert.Empty(await TitlesAsync(new ItemQuery { Kind = Kind, Directory = @"C:\lib", IncludeSubdirectories = false }));
    }

    [Fact]
    public async Task QueryDirectoryUsesARangeNotLike()
    {
        foreach (var path in new[]
        {
            @"C:\lib\a\x.png", @"C:\lib\a\sub\y.png", @"C:\lib\a b\z.png", @"C:\lib\a_b\w.png", @"C:\lib\a%\v.png", @"C:\lib\ab\u.png",
            @"C:\lib\A\deeper\still\t.png", @"C:\root.png",
        })
        {
            await _lib.AddAsync(Kind, path);
        }

        async Task<string[]> Names(string directory, bool subdirectories = true) =>
            [.. (await TitlesAsync(new ItemQuery { Kind = Kind, Directory = directory, IncludeSubdirectories = subdirectories })).Order()];

        Assert.Equal(["t", "x", "y"], await Names(@"C:\lib\a"));
        Assert.Equal(["x"], await Names(@"C:\lib\a", subdirectories: false));
        Assert.Equal(["z"], await Names(@"C:\lib\a b"));
        Assert.Equal(["w"], await Names(@"C:\lib\a_b"));
        Assert.Equal(["v"], await Names(@"C:\lib\a%"));
        Assert.Equal(["u"], await Names(@"C:\lib\ab"));
        Assert.Equal(["root", "t", "u", "v", "w", "x", "y", "z"], await Names(@"C:\"));
        Assert.Equal(["root"], await Names(@"C:\", subdirectories: false));
    }

    [Fact]
    public async Task QuerySourceTagsFavoriteRatingStatus()
    {
        var seed = await SeedAsync();
        Assert.Equal(["Gamma_100%"], await TitlesAsync(new ItemQuery { Kind = Kind, SourceId = seed.Source.Id }));
        Assert.Equal(["Delta", "Gamma_100%"], await TitlesAsync(new ItemQuery { Kind = Kind, SourceId = seed.Source.Id, IncludeMissing = true }));

        Assert.Equal(["Alpha", "Beta"], await TitlesAsync(new ItemQuery { Kind = Kind, Tags = ["cat"] }));
        Assert.Equal(["Alpha"], await TitlesAsync(new ItemQuery { Kind = Kind, Tags = ["CAT", "blue"] }));
        Assert.Empty(await TitlesAsync(new ItemQuery { Kind = Kind, Tags = ["cat", "dog"] }));

        Assert.Equal(["Alpha"], await TitlesAsync(new ItemQuery { Kind = Kind, FavoritesOnly = true }));
        Assert.Equal(["Alpha", "Beta"], await TitlesAsync(new ItemQuery { Kind = Kind, MinRating = 3 }));
        Assert.Equal(["Alpha"], await TitlesAsync(new ItemQuery { Kind = Kind, MinRating = 4 }));
        Assert.Equal(["Gamma_100%"], await TitlesAsync(new ItemQuery { Kind = Kind, Status = ItemStatus.InProgress }));
        Assert.Equal(["Alpha"], await TitlesAsync(new ItemQuery { Kind = Kind, Status = ItemStatus.Completed }));
    }

    [Fact]
    public async Task QueryCombinesFilters()
    {
        await SeedAsync();
        Assert.Equal(["Alpha"], await TitlesAsync(new ItemQuery { Kind = Kind, Directory = @"C:\lib\a", Tags = ["cat"], MinRating = 4 }));
        Assert.Equal(["Beta"], await TitlesAsync(new ItemQuery { Kind = Kind, Directory = @"C:\lib\a", Text = "bet", Tags = ["cat"] }));
        Assert.Empty(await TitlesAsync(new ItemQuery { Kind = Kind, Directory = @"C:\lib\b", Tags = ["cat"] }));
        Assert.Equal(["Alpha"], await TitlesAsync(new ItemQuery { Kind = Kind, FavoritesOnly = true, Status = ItemStatus.Completed, MinRating = 5, Text = "alp" }));
        Assert.Empty(await TitlesAsync(new ItemQuery { Kind = Kind, FavoritesOnly = true, Status = ItemStatus.InProgress }));
    }

    [Fact]
    public async Task QueryLoadsTags()
    {
        var seed = await SeedAsync();
        var items = await Service.QueryAsync(new ItemQuery { Kind = Kind }, Ct);
        Assert.Equal(["Blue", "cat"], items.Single(i => i.Id == seed.A.Id).Tags);
        Assert.Equal(["cat"], items.Single(i => i.Id == seed.B.Id).Tags);
        Assert.Empty(items.Single(i => i.Id == seed.C.Id).Tags);
    }

    [Fact]
    public async Task QuerySortsAndLimitsAfterSorting()
    {
        var seed = await SeedAsync();
        await _lib.ExecAsync("UPDATE item SET added_at = CASE id WHEN $a THEN 300 WHEN $b THEN 100 WHEN $c THEN 200 ELSE 0 END, " +
                       "file_size = CASE id WHEN $a THEN 1 WHEN $b THEN 3 WHEN $c THEN 2 ELSE 0 END, " +
                       "file_mtime = CASE id WHEN $a THEN 20 WHEN $b THEN 10 WHEN $c THEN 30 ELSE 0 END " +
                       "WHERE kind = 'illustration'", ("$a", seed.A.Id), ("$b", seed.B.Id), ("$c", seed.C.Id));
        await Service.UpdateAsync(seed.A.Id, new ItemPatch { AddTimeSpentSeconds = 5, ProgressPosition = 50, ProgressTotal = 100 });
        await Service.UpdateAsync(seed.B.Id, new ItemPatch { AddTimeSpentSeconds = 50, ProgressPosition = 1, ProgressTotal = 10 });
        // C: no progress total -> 0 even with a position
        await Service.UpdateAsync(seed.C.Id, new ItemPatch { AddTimeSpentSeconds = 20, ProgressPosition = 5 });
        await Service.UpdateAsync(seed.B.Id, new ItemPatch { MarkOpened = true });
        await Task.Delay(5, Ct);
        await Service.UpdateAsync(seed.A.Id, new ItemPatch { MarkOpened = true });

        async Task<string[]> Order(ItemSort sort, bool descending = false, int? limit = null) =>
            await TitlesAsync(new ItemQuery { Kind = Kind, Sort = sort, Descending = descending, Limit = limit });

        Assert.Equal(["Beta", "Gamma_100%", "Alpha"], await Order(ItemSort.AddedAt));
        Assert.Equal(["Alpha", "Gamma_100%", "Beta"], await Order(ItemSort.AddedAt, descending: true));
        Assert.Equal(["Gamma_100%", "Beta", "Alpha"], await Order(ItemSort.Rating));
        Assert.Equal(["Alpha", "Beta", "Gamma_100%"], await Order(ItemSort.Rating, descending: true));
        Assert.Equal(["Alpha", "Gamma_100%", "Beta"], await Order(ItemSort.FileSize));
        Assert.Equal(["Beta", "Alpha", "Gamma_100%"], await Order(ItemSort.FileModified));
        Assert.Equal(["Alpha", "Gamma_100%", "Beta"], await Order(ItemSort.TimeSpent));
        Assert.Equal(["Gamma_100%", "Beta", "Alpha"], await Order(ItemSort.Progress));
        Assert.Equal(["Alpha", "Beta", "Gamma_100%"], await Order(ItemSort.Progress, descending: true));
        Assert.Equal(["Alpha", "Beta"], await Order(ItemSort.LastOpenedAt, descending: true, limit: 2));
        Assert.Equal(["Gamma_100%", "Beta"], await Order(ItemSort.LastOpenedAt, limit: 2)); // never opened sorts first

        Assert.Equal(["Alpha", "Beta"], await Order(ItemSort.Title, limit: 2));
        Assert.Equal(["Gamma_100%", "Beta"], await Order(ItemSort.Title, descending: true, limit: 2));
        Assert.Equal(["Gamma_100%"], await Order(ItemSort.Rating, limit: 1));
        Assert.Empty(await Order(ItemSort.Rating, limit: 0));
    }

    [Fact]
    public async Task QueryTiesAreBrokenById()
    {
        var first = await _lib.AddAsync(Kind, @"C:\lib\a\one.png", "Same");
        var second = await _lib.AddAsync(Kind, @"C:\lib\a\two.png", "Same");
        await _lib.ExecAsync("UPDATE item SET added_at = 5");
        var byRating = await Service.QueryAsync(new ItemQuery { Kind = Kind, Sort = ItemSort.Rating }, Ct);
        Assert.Equal([first.Id, second.Id], byRating.Select(i => i.Id));
        var byAdded = await Service.QueryAsync(new ItemQuery { Kind = Kind, Sort = ItemSort.AddedAt, Descending = true }, Ct);
        Assert.Equal([first.Id, second.Id], byAdded.Select(i => i.Id));
        var byTitle = await Service.QueryAsync(new ItemQuery { Kind = Kind }, Ct);
        Assert.Equal([first.Id, second.Id], byTitle.Select(i => i.Id));
    }

    [Fact]
    public async Task TitleAndPathSortNaturally()
    {
        foreach (var n in new[] { 10, 2, 1, 20, 3 })
        {
            await _lib.AddAsync(Kind, $@"C:\lib\a\img {n}.png", $"Image {n}");
        }

        await _lib.AddAsync(Kind, @"C:\lib\a\IMG 15.png", "image 15");

        Assert.Equal(["Image 1", "Image 2", "Image 3", "Image 10", "image 15", "Image 20"], await TitlesAsync(new ItemQuery { Kind = Kind }));
        Assert.Equal(["Image 20", "image 15", "Image 10", "Image 3", "Image 2", "Image 1"], await TitlesAsync(new ItemQuery { Kind = Kind, Descending = true }));
        Assert.Equal(["Image 1", "Image 2", "Image 3", "Image 10", "image 15", "Image 20"], await TitlesAsync(new ItemQuery { Kind = Kind, Sort = ItemSort.Path }));
        Assert.Equal(["Image 1", "Image 2", "Image 3"], await TitlesAsync(new ItemQuery { Kind = Kind, Limit = 3 }));
    }

    [Fact]
    public async Task DirectoriesListsNonMissingFoldersNaturally()
    {
        await _lib.AddAsync(Kind, @"C:\lib\vol 10\a.png");
        await _lib.AddAsync(Kind, @"C:\lib\vol 2\a.png");
        await _lib.AddAsync(Kind, @"C:\lib\vol 2\b.png");
        var hidden = await _lib.AddAsync(Kind, @"C:\lib\gone\a.png");
        await _lib.AddAsync("comic", @"C:\lib\comics\a");
        await _lib.ExecAsync("UPDATE item SET missing = 1 WHERE id = $id", ("$id", hidden.Id));

        Assert.Equal([@"C:\lib\vol 2", @"C:\lib\vol 10"], await Service.GetDirectoriesAsync(Kind));
        Assert.Equal([@"C:\lib\comics"], await Service.GetDirectoriesAsync("comic"));
    }

    // ----- sources ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddSourceRejectsEqualParentAndChildFolders()
    {
        var source = await Service.AddSourceAsync(Kind, @"C:\pics\sub");
        Assert.Equal(@"C:\pics\sub", source.Path);
        Assert.Equal(0, source.ItemCount);
        Assert.Null(source.LastScanAt);

        await Assert.ThrowsAsync<ArgumentException>(() => Service.AddSourceAsync(Kind, @"C:\pics\sub"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service.AddSourceAsync(Kind, @"c:\PICS\SUB\"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service.AddSourceAsync(Kind, @"C:\pics"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service.AddSourceAsync(Kind, @"C:\"));
        await Assert.ThrowsAsync<ArgumentException>(() => Service.AddSourceAsync(Kind, @"C:\pics\sub\deeper"));

        // Similar names, other kinds and other drives are fine.
        await Service.AddSourceAsync(Kind, @"C:\pics\sub2");
        await Service.AddSourceAsync(Kind, @"C:\pics\su");
        await Service.AddSourceAsync("comic", @"C:\pics");
        await Service.AddSourceAsync(Kind, @"D:\pics");

        Assert.Equal([@"C:\pics\su", @"C:\pics\sub", @"C:\pics\sub2", @"D:\pics"], (await Service.GetSourcesAsync(Kind)).Select(s => s.Path));
        Assert.Equal([@"C:\pics"], (await Service.GetSourcesAsync("comic")).Select(s => s.Path));
    }

    [Fact]
    public async Task SourcesReportTheirItemCount()
    {
        var source = await Service.AddSourceAsync(Kind, @"C:\pics");
        var item = await _lib.AddAsync(Kind, @"C:\pics\a.png");
        await _lib.AddAsync(Kind, @"C:\elsewhere\b.png");
        await _lib.ExecAsync("UPDATE item SET source_id = $s WHERE id = $id", ("$s", source.Id), ("$id", item.Id));

        Assert.Equal(1, Assert.Single(await Service.GetSourcesAsync(Kind)).ItemCount);
    }

    [Fact]
    public async Task RemoveSourceDeletesItsItemsAndTagLinksOnly()
    {
        var source = await Service.AddSourceAsync(Kind, @"C:\pics");
        var inside = await _lib.AddAsync(Kind, @"C:\pics\a.png");
        var manual = await _lib.AddAsync(Kind, @"C:\other\b.png");
        await _lib.ExecAsync("UPDATE item SET source_id = $s WHERE id = $id", ("$s", source.Id), ("$id", inside.Id));
        await Service.SetTagsAsync(inside.Id, ["only-in-source", "shared"]);
        await Service.SetTagsAsync(manual.Id, ["shared"]);

        await Service.RemoveSourceAsync(source.Id);

        Assert.Empty(await Service.GetSourcesAsync(Kind));
        Assert.Null(await Service.GetAsync(inside.Id));
        Assert.NotNull(await Service.GetAsync(manual.Id));
        Assert.Equal(1, await _lib.ScalarAsync<int>("SELECT COUNT(*) FROM item"));
        Assert.Equal(1, await _lib.ScalarAsync<int>("SELECT COUNT(*) FROM item_tag"));
        Assert.Equal([new TagCount("shared", 1)], await Service.GetTagsAsync(Kind));
        Assert.Equal(new LibraryChanged(Kind, LibraryChangeType.Reset, []), _lib.Events[^1], new ChangedComparer());

        await Service.RemoveSourceAsync(source.Id); // unknown now: harmless
    }

    // ----- scale -----------------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Perf")]
    public async Task QueryOf100000RowsTakesUnderOneSecond()
    {
        await Service.Ready;
        const int count = 100_000;
        Service.Database.Write(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using (var insert = connection.Command(
                       "INSERT INTO item (kind, path, dir, title, file_size, file_mtime, added_at) VALUES ($kind, $path, $dir, $title, $size, $mtime, $added)",
                       transaction))
            {
                insert.Parameters.AddWithValue("$kind", Kind);
                var path = insert.Parameters.Add("$path", SqliteType.Text);
                var dir = insert.Parameters.Add("$dir", SqliteType.Text);
                var title = insert.Parameters.Add("$title", SqliteType.Text);
                var size = insert.Parameters.Add("$size", SqliteType.Integer);
                var mtime = insert.Parameters.Add("$mtime", SqliteType.Integer);
                var added = insert.Parameters.Add("$added", SqliteType.Integer);
                for (var i = 0; i < count; i++)
                {
                    var folder = $@"C:\perf\folder {i % 200}";
                    path.Value = $@"{folder}\image {i}.png";
                    dir.Value = folder;
                    title.Value = $"Image {i}";
                    size.Value = i;
                    mtime.Value = i;
                    added.Value = i;
                    insert.ExecuteNonQuery();
                }
            }

            connection.Command("INSERT INTO tag (name) SELECT 'tag ' || value FROM (WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM n WHERE value < 50) SELECT value FROM n)", transaction).Execute();
            connection.Command("INSERT INTO item_tag (item_id, tag_id) SELECT i.id, (i.id % 50) + 1 FROM item i WHERE i.id % 10 = 0", transaction).Execute();
            transaction.Commit();
            return 0;
        });

        await Service.QueryAsync(new ItemQuery { Kind = Kind, Limit = 1 }, Ct); // warm-up: JIT and connection pool

        var stopwatch = Stopwatch.StartNew();
        var all = await Service.QueryAsync(new ItemQuery { Kind = Kind }, Ct);
        var titleSort = stopwatch.Elapsed;
        stopwatch.Restart();
        var byAdded = await Service.QueryAsync(new ItemQuery { Kind = Kind, Sort = ItemSort.AddedAt, Descending = true }, Ct);
        var sqlSort = stopwatch.Elapsed;
        stopwatch.Restart();
        var filtered = await Service.QueryAsync(new ItemQuery { Kind = Kind, Directory = @"C:\perf", Text = "image 99", Tags = ["tag 1"] }, Ct);
        var filter = stopwatch.Elapsed;

        TestContext.Current.SendDiagnosticMessage(
            $"100k rows: natural title sort {titleSort.TotalMilliseconds:F0} ms, SQL sort {sqlSort.TotalMilliseconds:F0} ms, filtered {filter.TotalMilliseconds:F0} ms");
        Assert.Equal(count, all.Count);
        Assert.Equal(count, byAdded.Count);
        Assert.Equal("Image 0", all[0].Title);
        Assert.Equal("Image 99999", all[^1].Title);
        Assert.Equal(count, byAdded[0].Id);
        Assert.NotEmpty(filtered);
        Assert.True(titleSort < TimeSpan.FromSeconds(1), $"natural title sort took {titleSort.TotalMilliseconds:F0} ms");
        Assert.True(sqlSort < TimeSpan.FromSeconds(1), $"SQL sort took {sqlSort.TotalMilliseconds:F0} ms");
    }

    private sealed class ChangedComparer : IEqualityComparer<LibraryChanged>
    {
        public bool Equals(LibraryChanged? x, LibraryChanged? y) =>
            x is not null && y is not null && x.Kind == y.Kind && x.Type == y.Type && x.ItemIds.SequenceEqual(y.ItemIds);

        public int GetHashCode(LibraryChanged obj) => HashCode.Combine(obj.Kind, obj.Type);
    }
}
