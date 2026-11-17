using System.Diagnostics;
using Magpienest.Sdk.Library;
using Microsoft.Data.Sqlite;

namespace Magpienest.Plugins.Library.Tests;

/// <summary>Tags and ratings on folders, and how they reach the items below them.</summary>
public sealed class LibraryFolderLabelTests : IDisposable
{
    private const string Kind = "illustration";
    private readonly TestLibrary _lib = new();

    private LibraryService Service => _lib.Service;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _lib.Dispose();

    [Fact]
    public async Task LabelsAreStoredReplacedAndRemoved()
    {
        await Service.SetFolderLabelsAsync(Kind, @"C:\lib\trip\", 4, ["Scenery", "kyoto"]);
        var labels = Assert.Single(await Service.GetFolderLabelsAsync(Kind));
        Assert.Equal(@"C:\lib\trip", labels.Path);
        Assert.Equal(4, labels.Rating);
        Assert.Equal(["kyoto", "Scenery"], labels.Tags);
        Assert.Contains(_lib.Events, e => e is { Kind: Kind, Type: LibraryChangeType.Reset });
        Assert.Empty(await Service.GetFolderLabelsAsync("comic"));

        await Service.SetFolderLabelsAsync(Kind, @"c:\LIB\trip", 2, ["night"]);
        labels = Assert.Single(await Service.GetFolderLabelsAsync(Kind));
        Assert.Equal(2, labels.Rating);
        Assert.Equal(["night"], labels.Tags);

        await Service.SetFolderLabelsAsync(Kind, @"C:\lib\trip", 0, []);
        Assert.Empty(await Service.GetFolderLabelsAsync(Kind));
        Assert.Equal(0, await _lib.ScalarAsync<int>("SELECT COUNT(*) FROM tag"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service.SetFolderLabelsAsync(Kind, @"C:\lib", 6, []));
    }

    [Fact]
    public async Task AFolderTagReachesEveryItemBelowTheFolderOnly()
    {
        var direct = await _lib.AddAsync(Kind, @"C:\lib\trip\a.png");
        var nested = await _lib.AddAsync(Kind, @"C:\lib\trip\day 2\b.png");
        await _lib.AddAsync(Kind, @"C:\lib\trips\c.png");     // same prefix, another folder
        await _lib.AddAsync(Kind, @"C:\lib\d.png");
        var tagged = await _lib.AddAsync(Kind, @"C:\lib\other\e.png");
        await Service.SetTagsAsync(tagged.Id, ["scenery"]);
        await Service.SetFolderLabelsAsync(Kind, @"C:\lib\trip", 0, ["Scenery"]);

        var found = await Service.QueryAsync(new ItemQuery { Kind = Kind, Tags = ["scenery"], Sort = ItemSort.Path }, Ct);
        Assert.Equal(new[] { direct.Id, nested.Id, tagged.Id }.Order(), found.Select(i => i.Id).Order());

        var searched = await Service.QueryAsync(new ItemQuery { Kind = Kind, Text = "scen" }, Ct);
        Assert.Equal(3, searched.Count);

        var item = (await Service.GetAsync(nested.Id))!;
        Assert.Equal(["scenery"], item.FolderTags); // the tag already existed with this spelling
        Assert.Empty(item.Tags);
    }

    [Fact]
    public async Task TagsAddUpDownTheTreeAndBothMustMatch()
    {
        var item = await _lib.AddAsync(Kind, @"C:\lib\trip\day 2\b.png");
        await _lib.AddAsync(Kind, @"C:\lib\trip\a.png");
        await Service.SetFolderLabelsAsync(Kind, @"C:\lib\trip", 0, ["trip"]);
        await Service.SetFolderLabelsAsync(Kind, @"C:\lib\trip\day 2", 0, ["night", "TRIP"]);

        var both = Assert.Single(await Service.QueryAsync(new ItemQuery { Kind = Kind, Tags = ["trip", "night"] }, Ct));
        Assert.Equal(item.Id, both.Id);
        Assert.Equal(["night", "trip"], both.FolderTags.Select(t => t.ToLowerInvariant()));
    }

    [Fact]
    public async Task TheItemsOwnRatingComesFirstThenTheNearestRatedFolder()
    {
        var own = await _lib.AddAsync(Kind, @"C:\lib\trip\own.png");
        await Service.UpdateAsync(own.Id, new ItemPatch { Rating = 1 });
        var top = await _lib.AddAsync(Kind, @"C:\lib\trip\top.png");
        var deep = await _lib.AddAsync(Kind, @"C:\lib\trip\day 2\deep.png");
        var unrated = await _lib.AddAsync(Kind, @"C:\lib\elsewhere\none.png");
        await Service.SetFolderLabelsAsync(Kind, @"C:\lib\trip", 5, []);
        await Service.SetFolderLabelsAsync(Kind, @"C:\lib\trip\day 2", 3, ["night"]);

        var items = (await Service.QueryAsync(new ItemQuery { Kind = Kind }, Ct)).ToDictionary(i => i.Id);
        Assert.Equal((1, 5), (items[own.Id].Rating, items[own.Id].FolderRating));
        Assert.Equal(5, items[top.Id].FolderRating);
        Assert.Equal(3, items[deep.Id].FolderRating);
        Assert.Equal(0, items[unrated.Id].FolderRating);

        var four = await Service.QueryAsync(new ItemQuery { Kind = Kind, MinRating = 4 }, Ct);
        Assert.Equal([top.Id], four.Select(i => i.Id));
        var three = await Service.QueryAsync(new ItemQuery { Kind = Kind, MinRating = 3 }, Ct);
        Assert.Equal(new[] { deep.Id, top.Id }.Order(), three.Select(i => i.Id).Order());

        var sorted = await Service.QueryAsync(new ItemQuery { Kind = Kind, Sort = ItemSort.Rating, Descending = true }, Ct);
        Assert.Equal([top.Id, deep.Id, own.Id, unrated.Id], sorted.Select(i => i.Id));
    }

    [Fact]
    public async Task TagCountsIncludeItemsBelowTaggedFoldersOnce()
    {
        var a = await _lib.AddAsync(Kind, @"C:\lib\trip\a.png");
        await _lib.AddAsync(Kind, @"C:\lib\trip\b.png");
        var c = await _lib.AddAsync(Kind, @"C:\lib\other\c.png");
        await Service.SetTagsAsync(a.Id, ["scenery", "cat"]);
        await Service.SetTagsAsync(c.Id, ["scenery"]);
        await Service.SetFolderLabelsAsync(Kind, @"C:\lib\trip", 0, ["scenery", "kyoto"]);
        await Service.SetFolderLabelsAsync(Kind, @"C:\lib\empty", 0, ["unused"]);

        var tags = await Service.GetTagsAsync(Kind);
        Assert.Equal([new TagCount("cat", 1), new TagCount("kyoto", 2), new TagCount("scenery", 3)], tags);
    }

    [Fact]
    public async Task ATagOnlyAFolderCarriesIsKeptWhenItemsDropIt()
    {
        var item = await _lib.AddAsync(Kind, @"C:\lib\trip\a.png");
        await Service.SetTagsAsync(item.Id, ["kyoto"]);
        await Service.SetFolderLabelsAsync(Kind, @"C:\lib\trip", 0, ["kyoto"]);
        await Service.SetTagsAsync(item.Id, []);
        await Service.RemoveAsync([item.Id]);

        Assert.Equal(["kyoto"], Assert.Single(await Service.GetFolderLabelsAsync(Kind)).Tags);
        Assert.Equal(1, await _lib.ScalarAsync<int>("SELECT COUNT(*) FROM tag"));
    }

    [Fact]
    public void AVersionOneDatabaseIsUpgradedWithItsData()
    {
        var path = Path.Combine(_lib.Root, "upgrade", "library.db");
        using (var old = new LibraryDb(path))
        {
            old.Write(connection =>
            {
                using var transaction = connection.BeginTransaction();
                foreach (var statement in LibraryDb.Migrations.Single(m => m.Version == 1).Sql.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!string.IsNullOrWhiteSpace(statement))
                    {
                        connection.Command(statement, transaction).Execute();
                    }
                }

                connection.Command("INSERT INTO tag (name) VALUES ('old')", transaction).Execute();
                connection.Command("PRAGMA user_version = 1", transaction).Execute();
                transaction.Commit();
                return 0;
            });
        }

        using var db = new LibraryDb(path);
        Assert.Equal(LibraryDb.Migrations.Count(m => m.Version > 1), db.Migrate());
        Assert.Equal(1, db.Read(connection => Convert.ToInt32(connection.Command("SELECT COUNT(*) FROM tag").Scalar())));
        Assert.Equal(0, db.Read(connection => Convert.ToInt32(connection.Command("SELECT COUNT(*) FROM folder").Scalar())));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task FiltersOver100000RowsWithLabelledFoldersTakeUnderOneSecond()
    {
        await Service.Ready;
        const int count = 100_000;
        Service.Database.Write(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var insert = connection.Command(
                "INSERT INTO item (kind, path, dir, title, rating, added_at) VALUES ($kind, $path, $dir, $title, $rating, 0)", transaction);
            insert.Parameters.AddWithValue("$kind", Kind);
            var path = insert.Parameters.Add("$path", SqliteType.Text);
            var dir = insert.Parameters.Add("$dir", SqliteType.Text);
            var title = insert.Parameters.Add("$title", SqliteType.Text);
            var rating = insert.Parameters.Add("$rating", SqliteType.Integer);
            for (var i = 0; i < count; i++)
            {
                var folder = $@"C:\perf\group {i % 20}\folder {i % 200}";
                path.Value = $@"{folder}\image {i}.png";
                dir.Value = folder;
                title.Value = $"Image {i}";
                rating.Value = i % 7 == 0 ? 2 : 0;
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return 0;
        });

        // 120 labelled folders: every group, and 100 of the folders.
        for (var g = 0; g < 20; g++)
        {
            await Service.SetFolderLabelsAsync(Kind, $@"C:\perf\group {g}", g % 5 + 1, [$"group tag {g % 4}"]);
        }

        for (var f = 0; f < 100; f++)
        {
            await Service.SetFolderLabelsAsync(Kind, $@"C:\perf\group {f % 20}\folder {f}", f % 3, [$"folder tag {f % 10}"]);
        }

        await Service.QueryAsync(new ItemQuery { Kind = Kind, Limit = 1 }, Ct); // warm-up

        var stopwatch = Stopwatch.StartNew();
        var rated = await Service.QueryAsync(new ItemQuery { Kind = Kind, MinRating = 4, Sort = ItemSort.Rating, Descending = true }, Ct);
        var ratingTime = stopwatch.Elapsed;
        stopwatch.Restart();
        var tagged = await Service.QueryAsync(new ItemQuery { Kind = Kind, Tags = ["group tag 1", "folder tag 3"], Sort = ItemSort.AddedAt }, Ct);
        var tagTime = stopwatch.Elapsed;
        stopwatch.Restart();
        var counts = await Service.GetTagsAsync(Kind);
        var countTime = stopwatch.Elapsed;

        TestContext.Current.SendDiagnosticMessage(
            $"100k rows, 120 labelled folders: rating filter+sort {ratingTime.TotalMilliseconds:F0} ms, tag filter {tagTime.TotalMilliseconds:F0} ms, tag counts {countTime.TotalMilliseconds:F0} ms");
        Assert.NotEmpty(rated);
        Assert.All(rated, i => Assert.True((i.Rating > 0 ? i.Rating : i.FolderRating) >= 4));
        Assert.NotEmpty(tagged);
        Assert.Equal(14, counts.Count);
        Assert.True(ratingTime < TimeSpan.FromSeconds(1), $"rating filter took {ratingTime.TotalMilliseconds:F0} ms");
        Assert.True(tagTime < TimeSpan.FromSeconds(1), $"tag filter took {tagTime.TotalMilliseconds:F0} ms");
        Assert.True(countTime < TimeSpan.FromSeconds(1), $"tag counts took {countTime.TotalMilliseconds:F0} ms");
    }
}
