using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.Library.Tests;

/// <summary>Kinds, scanning and LibraryChanged events.</summary>
public sealed class LibraryScanTests : IDisposable
{
    private const string Kind = "illustration";
    private readonly TestLibrary _lib = new();

    private LibraryService Service => _lib.Service;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _lib.Dispose();

    private async Task<(LibrarySource Source, FakeScanner Scanner)> FakeSourceAsync()
    {
        var scanner = new FakeScanner();
        _lib.RegisterScanner(Kind, scanner);
        var source = await Service.AddSourceAsync(Kind, _lib.NewFolder("fake"));
        return (source, scanner);
    }

    private async Task<(LibrarySource Source, string Root)> RealSourceAsync()
    {
        _lib.RegisterScanner(Kind, new FolderScanner());
        var root = _lib.NewFolder("real");
        var source = await Service.AddSourceAsync(Kind, root);
        return (source, root);
    }

    private static string WriteFile(string path, int size, int mtimeSeconds = 1000)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + mtimeSeconds).UtcDateTime);
        return path;
    }

    private async Task<IReadOnlyList<LibraryItem>> AllAsync() =>
        await Service.QueryAsync(new ItemQuery { Kind = Kind, IncludeMissing = true }, Ct);

    // ----- kinds -----------------------------------------------------------------------------------------------

    [Fact]
    public void KindsAreRegisteredAndRemovedWithTheirOwner()
    {
        var first = new ItemKindDescriptor { Id = "illustration", DisplayNameKey = "Test.Kind" };
        var second = new ItemKindDescriptor { Id = "comic", DisplayNameKey = "Test.Kind" };
        var owner = new FakeContext();
        var other = new FakeContext();

        Service.RegisterKind(owner, first);
        Service.RegisterKind(other, second);
        Assert.Equal(["illustration", "comic"], Service.Kinds.Select(k => k.Id));
        Assert.Throws<InvalidOperationException>(() => Service.RegisterKind(other, new ItemKindDescriptor { Id = "comic", DisplayNameKey = "x" }));

        owner.Dispose();
        Assert.Equal(["comic"], Service.Kinds.Select(k => k.Id));
        Service.RegisterKind(owner, first); // can be registered again after its owner was deactivated
        other.Dispose();
        Assert.Equal(["illustration"], Service.Kinds.Select(k => k.Id));
        owner.Dispose();
        Assert.Empty(Service.Kinds);
    }

    [Fact]
    public async Task ScanWithoutAScannerThrows()
    {
        var source = await Service.AddSourceAsync("nokind", _lib.NewFolder("nokind"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ScanAsync(source.Id, null, Ct));

        Service.RegisterKind(_lib.Context, new ItemKindDescriptor { Id = "nokind", DisplayNameKey = "x" }); // manual-only kind
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ScanAsync(source.Id, null, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service.ScanAsync(source.Id + 100, null, Ct));
    }

    // ----- basic scanning --------------------------------------------------------------------------------------

    [Fact]
    public async Task FirstScanAddsAndSecondScanAddsNothing()
    {
        var (source, root) = await RealSourceAsync();
        WriteFile(Path.Combine(root, "a", "one.png"), 10);
        WriteFile(Path.Combine(root, "a", "two.png"), 20);
        WriteFile(Path.Combine(root, "three.png"), 30);

        var first = await Service.ScanAsync(source.Id, null, Ct);
        Assert.Equal(new ScanResult(3, 0, 0, 0, 3), first);
        var items = await AllAsync();
        Assert.Equal(["one", "three", "two"], items.Select(i => i.Title));
        Assert.All(items, i => Assert.Equal(source.Id, i.SourceId));
        Assert.Equal(Path.Combine(root, "a"), items.Single(i => i.Title == "one").Directory);
        Assert.Equal(10, items.Single(i => i.Title == "one").FileSize);

        var second = await Service.ScanAsync(source.Id, null, Ct);
        Assert.Equal(new ScanResult(0, 0, 0, 0, 3), second);
        Assert.Equal(3, (await AllAsync()).Count);

        var stored = Assert.Single(await Service.GetSourcesAsync(Kind));
        Assert.NotNull(stored.LastScanAt);
        Assert.Equal(3, stored.ItemCount);
    }

    [Fact]
    public async Task EachScanAnnouncesOneReset()
    {
        var (source, scanner) = await FakeSourceAsync();
        scanner.Entries.Add(FakeScanner.Entry(Path.Combine(_lib.Root, "files", "fake", "a.png")));
        await Service.ScanAsync(source.Id, null, Ct);
        var resets = _lib.Events.Where(e => e.Type == LibraryChangeType.Reset).ToList();
        Assert.Single(resets);
        Assert.Equal(Kind, resets[0].Kind);
        Assert.Empty(resets[0].ItemIds);
        Assert.Single(_lib.Events); // scanning does not announce single items

        await Service.ScanAsync(source.Id, null, Ct);
        Assert.Equal(2, _lib.Events.Count(e => e.Type == LibraryChangeType.Reset));
    }

    [Fact]
    public async Task ChangedSizeOrModifiedTimeUpdatesTheRow()
    {
        var (source, root) = await RealSourceAsync();
        var file = WriteFile(Path.Combine(root, "one.png"), 10, mtimeSeconds: 1000);
        await Service.ScanAsync(source.Id, null, Ct);
        var original = Assert.Single(await AllAsync());

        WriteFile(file, 25, mtimeSeconds: 1000);
        Assert.Equal(new ScanResult(0, 1, 0, 0, 1), await Service.ScanAsync(source.Id, null, Ct));
        var resized = Assert.Single(await AllAsync());
        Assert.Equal(original.Id, resized.Id);
        Assert.Equal(25, resized.FileSize);

        File.SetLastWriteTimeUtc(file, DateTimeOffset.FromUnixTimeSeconds(1_700_005_000).UtcDateTime);
        Assert.Equal(new ScanResult(0, 1, 0, 0, 1), await Service.ScanAsync(source.Id, null, Ct));
        var touched = Assert.Single(await AllAsync());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_005_000), touched.FileModified);
    }

    [Fact]
    public async Task DeletedFileIsFlaggedMissingAndComesBack()
    {
        var (source, root) = await RealSourceAsync();
        var keep = WriteFile(Path.Combine(root, "keep.png"), 10);
        var file = WriteFile(Path.Combine(root, "gone.png"), 20);
        await Service.ScanAsync(source.Id, null, Ct);
        var gone = (await AllAsync()).Single(i => i.Title == "gone");
        await Service.SetTagsAsync(gone.Id, ["precious"]);
        await Service.UpdateAsync(gone.Id, new ItemPatch { Rating = 4 });

        File.Delete(file);
        Assert.Equal(new ScanResult(0, 0, 1, 0, 1), await Service.ScanAsync(source.Id, null, Ct));
        var flagged = (await AllAsync()).Single(i => i.Title == "gone");
        Assert.True(flagged.IsMissing);
        Assert.Equal(["precious"], flagged.Tags);
        Assert.Equal(["keep"], (await Service.QueryAsync(new ItemQuery { Kind = Kind }, Ct)).Select(i => i.Title));

        // A second scan does not count it again.
        Assert.Equal(new ScanResult(0, 0, 0, 0, 1), await Service.ScanAsync(source.Id, null, Ct));

        WriteFile(file, 20);
        Assert.Equal(new ScanResult(0, 1, 0, 0, 2), await Service.ScanAsync(source.Id, null, Ct));
        var back = (await AllAsync()).Single(i => i.Title == "gone");
        Assert.False(back.IsMissing);
        Assert.Equal(gone.Id, back.Id);
        Assert.Equal(4, back.Rating);
        Assert.True(File.Exists(keep));
    }

    // ----- relinking -------------------------------------------------------------------------------------------

    [Fact]
    public async Task MovedFileIsRelinkedAndKeepsItsMetadata()
    {
        var (source, root) = await RealSourceAsync();
        var original = WriteFile(Path.Combine(root, "a", "p.png"), 40);
        await Service.ScanAsync(source.Id, null, Ct);
        var item = Assert.Single(await AllAsync());
        await Service.SetTagsAsync(item.Id, ["keep me"]);
        await Service.UpdateAsync(item.Id, new ItemPatch { Rating = 5, IsFavorite = true, Notes = "notes" });

        Directory.CreateDirectory(Path.Combine(root, "b"));
        File.Move(original, Path.Combine(root, "b", "p.png"));
        Assert.Equal(new ScanResult(0, 0, 0, 1, 1), await Service.ScanAsync(source.Id, null, Ct));

        var moved = Assert.Single(await AllAsync());
        Assert.Equal(item.Id, moved.Id);
        Assert.Equal(Path.Combine(root, "b", "p.png"), moved.Path);
        Assert.Equal(Path.Combine(root, "b"), moved.Directory);
        Assert.False(moved.IsMissing);
        Assert.Equal(["keep me"], moved.Tags);
        Assert.Equal(5, moved.Rating);
        Assert.True(moved.IsFavorite);
        Assert.Equal("notes", moved.Notes);
    }

    [Fact]
    public async Task FileRenamedInPlaceIsRelinkedAndAutoTitleFollows()
    {
        var (source, root) = await RealSourceAsync();
        var original = WriteFile(Path.Combine(root, "a", "old name.png"), 40);
        await Service.ScanAsync(source.Id, null, Ct);
        var item = Assert.Single(await AllAsync());
        Assert.Equal("old name", item.Title);
        await Service.SetTagsAsync(item.Id, ["t"]);

        File.Move(original, Path.Combine(root, "a", "new name.png"));
        Assert.Equal(new ScanResult(0, 0, 0, 1, 1), await Service.ScanAsync(source.Id, null, Ct));

        var renamed = Assert.Single(await AllAsync());
        Assert.Equal(item.Id, renamed.Id);
        Assert.Equal(Path.Combine(root, "a", "new name.png"), renamed.Path);
        Assert.Equal("new name", renamed.Title);
        Assert.Equal(["t"], renamed.Tags);
    }

    [Fact]
    public async Task RenamedItemWithEditedTitleKeepsItsTitle()
    {
        var (source, root) = await RealSourceAsync();
        var original = WriteFile(Path.Combine(root, "a", "old name.png"), 40);
        await Service.ScanAsync(source.Id, null, Ct);
        var item = Assert.Single(await AllAsync());
        await Service.UpdateAsync(item.Id, new ItemPatch { Title = "My own title" });

        File.Move(original, Path.Combine(root, "a", "new name.png"));
        await Service.ScanAsync(source.Id, null, Ct);

        var renamed = Assert.Single(await AllAsync());
        Assert.Equal(item.Id, renamed.Id);
        Assert.Equal("My own title", renamed.Title);
    }

    [Fact]
    public async Task RenamedAndMovedFileIsNotRelinked()
    {
        var (source, root) = await RealSourceAsync();
        var original = WriteFile(Path.Combine(root, "a", "p.png"), 40);
        await Service.ScanAsync(source.Id, null, Ct);
        var item = Assert.Single(await AllAsync());
        await Service.SetTagsAsync(item.Id, ["stays with old row"]);

        Directory.CreateDirectory(Path.Combine(root, "b"));
        File.Move(original, Path.Combine(root, "b", "q.png"));
        Assert.Equal(new ScanResult(1, 0, 1, 0, 1), await Service.ScanAsync(source.Id, null, Ct));

        var items = await AllAsync();
        Assert.Equal(2, items.Count);
        var old = items.Single(i => i.Id == item.Id);
        Assert.True(old.IsMissing);
        Assert.Equal(Path.Combine(root, "a", "p.png"), old.Path);
        Assert.Equal(["stays with old row"], old.Tags);
        var created = items.Single(i => i.Id != item.Id);
        Assert.False(created.IsMissing);
        Assert.Equal(Path.Combine(root, "b", "q.png"), created.Path);
        Assert.Empty(created.Tags);
    }

    [Fact]
    public async Task AmbiguousCandidatesAreNotRelinked()
    {
        var (source, root) = await RealSourceAsync();
        var first = WriteFile(Path.Combine(root, "a", "x.png"), 40);
        var second = WriteFile(Path.Combine(root, "b", "x.png"), 40);
        await Service.ScanAsync(source.Id, null, Ct);

        File.Delete(first);
        File.Delete(second);
        WriteFile(Path.Combine(root, "c", "x.png"), 40);
        Assert.Equal(new ScanResult(1, 0, 2, 0, 1), await Service.ScanAsync(source.Id, null, Ct));

        var items = await AllAsync();
        Assert.Equal(3, items.Count);
        Assert.Equal(2, items.Count(i => i.IsMissing));
        Assert.Equal(Path.Combine(root, "c", "x.png"), items.Single(i => !i.IsMissing).Path);
    }

    [Fact]
    public async Task ItemAlreadyMissingBeforeTheScanCanBeRelinked()
    {
        var (source, root) = await RealSourceAsync();
        var original = WriteFile(Path.Combine(root, "a", "p.png"), 40);
        await Service.ScanAsync(source.Id, null, Ct);
        var item = Assert.Single(await AllAsync());

        File.Move(original, Path.Combine(root, "p-hidden.tmp"));
        await Service.ScanAsync(source.Id, null, Ct); // new name in another folder: a new row is added, old one flagged
        File.Delete(Path.Combine(root, "p-hidden.tmp"));
        Directory.CreateDirectory(Path.Combine(root, "z"));
        WriteFile(Path.Combine(root, "z", "p.png"), 40);
        await Service.ScanAsync(source.Id, null, Ct);

        var all = await AllAsync();
        Assert.Contains(all, i => i.Id == item.Id && !i.IsMissing && i.Path == Path.Combine(root, "z", "p.png"));
    }

    [Fact]
    public async Task ManualItemInsideASourceIsAdopted()
    {
        var (source, root) = await RealSourceAsync();
        var path = WriteFile(Path.Combine(root, "mine.png"), 10);
        var manual = await _lib.AddAsync(Kind, path, "Hand-picked");
        Assert.Null(manual.SourceId);

        var result = await Service.ScanAsync(source.Id, null, Ct);
        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        var adopted = Assert.Single(await AllAsync());
        Assert.Equal(manual.Id, adopted.Id);
        Assert.Equal(source.Id, adopted.SourceId);
        Assert.Equal("Hand-picked", adopted.Title);
        Assert.Equal(10, adopted.FileSize);
    }

    // ----- titles ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task UserEditedTitleSurvivesARescan()
    {
        var (source, root) = await RealSourceAsync();
        var file = WriteFile(Path.Combine(root, "photo.png"), 10);
        await Service.ScanAsync(source.Id, null, Ct);
        var item = Assert.Single(await AllAsync());
        Assert.Equal("photo", item.Title);
        await Service.UpdateAsync(item.Id, new ItemPatch { Title = "Holiday" });

        WriteFile(file, 99); // content changed: the row is updated, the title is not
        Assert.Equal(1, (await Service.ScanAsync(source.Id, null, Ct)).Updated);
        var after = Assert.Single(await AllAsync());
        Assert.Equal("Holiday", after.Title);
        Assert.Equal(99, after.FileSize);
    }

    [Fact]
    public async Task AutoTitleFollowsTheScannerOnUpdate()
    {
        var (source, scanner) = await FakeSourceAsync();
        var path = Path.Combine(_lib.Root, "files", "fake", "doc.png");
        scanner.Entries.Add(FakeScanner.Entry(path, size: 10, title: "doc"));
        await Service.ScanAsync(source.Id, null, Ct);

        scanner.Entries[0] = FakeScanner.Entry(path, size: 11, title: "Document");
        await Service.ScanAsync(source.Id, null, Ct);
        Assert.Equal("Document", Assert.Single(await AllAsync()).Title);
    }

    // ----- unreachable root and cancellation -------------------------------------------------------------------

    [Fact]
    public async Task UnreachableRootMarksNothingMissingAndNotifies()
    {
        var (source, root) = await RealSourceAsync();
        WriteFile(Path.Combine(root, "a.png"), 10);
        WriteFile(Path.Combine(root, "b.png"), 20);
        await Service.ScanAsync(source.Id, null, Ct);
        var resetsBefore = _lib.Events.Count(e => e.Type == LibraryChangeType.Reset);
        Assert.Empty(_lib.Notices);

        Directory.Delete(root, recursive: true); // "unplugged"
        var result = await Service.ScanAsync(source.Id, null, Ct);

        Assert.Equal(new ScanResult(0, 0, 0, 0, 0), result);
        Assert.Single(_lib.Notices);
        var items = await AllAsync();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.False(i.IsMissing));
        Assert.Equal(resetsBefore, _lib.Events.Count(e => e.Type == LibraryChangeType.Reset));
    }

    [Fact]
    public async Task CancellationCommitsPartialWorkAndMarksNothingMissing()
    {
        var (source, scanner) = await FakeSourceAsync();
        var folder = Path.Combine(_lib.Root, "files", "fake");
        string P(string name) => Path.Combine(folder, name);
        foreach (var name in new[] { "a", "b", "c", "d", "e" })
        {
            scanner.Entries.Add(FakeScanner.Entry(P(name + ".png")));
        }

        await Service.ScanAsync(source.Id, null, Ct);
        Assert.Equal(5, (await AllAsync()).Count);

        // Second scan: a changed, two new files, then cancel before b is reported; c, d, e are never reached.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        scanner.Entries.Clear();
        scanner.Entries.Add(FakeScanner.Entry(P("a.png"), size: 555));
        scanner.Entries.Add(FakeScanner.Entry(P("new1.png")));
        scanner.Entries.Add(FakeScanner.Entry(P("new2.png")));
        scanner.Entries.Add(FakeScanner.Entry(P("b.png")));
        scanner.BeforeYield = index =>
        {
            if (index == 3)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.ScanAsync(source.Id, null, cts.Token));

        var items = await AllAsync();
        Assert.Equal(7, items.Count);
        Assert.All(items, i => Assert.False(i.IsMissing)); // c, d, e were not seen, but nothing is flagged
        Assert.Equal(555, items.Single(i => i.Path == P("a.png")).FileSize);
        Assert.Contains(items, i => i.Path == P("new1.png"));
        Assert.Contains(items, i => i.Path == P("new2.png"));
        Assert.Equal(2, _lib.Events.Count(e => e.Type == LibraryChangeType.Reset)); // first scan + the partial one

        // The next full scan finishes the job.
        scanner.BeforeYield = null;
        scanner.Entries.Clear();
        scanner.Entries.Add(FakeScanner.Entry(P("a.png"), size: 555));
        var finished = await Service.ScanAsync(source.Id, null, Ct);
        Assert.Equal(6, finished.MarkedMissing);
    }

    // ----- concurrency and progress ----------------------------------------------------------------------------

    [Fact]
    public async Task SecondRequestForTheSameSourceAwaitsTheRunningScan()
    {
        var (source, scanner) = await FakeSourceAsync();
        scanner.Entries.Add(FakeScanner.Entry(Path.Combine(_lib.Root, "files", "fake", "a.png")));
        using var release = new ManualResetEventSlim();
        scanner.OnStart = () => release.Wait(TestContext.Current.CancellationToken);

        var first = Service.ScanAsync(source.Id, null, Ct);
        while (scanner.ScanCount == 0)
        {
            await Task.Delay(5, Ct);
        }

        var second = Service.ScanAsync(source.Id, null, Ct);
        await Task.Delay(50, Ct);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        release.Set();
        var firstResult = await first;
        var secondResult = await second;
        Assert.Equal(1, scanner.ScanCount);
        Assert.Equal(firstResult, secondResult);
        Assert.Equal(new ScanResult(1, 0, 0, 0, 1), firstResult);

        // Once finished, a new request starts a new scan.
        scanner.OnStart = null;
        await Service.ScanAsync(source.Id, null, Ct);
        Assert.Equal(2, scanner.ScanCount);
    }

    [Fact]
    public async Task ProgressIsReportedEvery200Entries()
    {
        var (source, scanner) = await FakeSourceAsync();
        var folder = Path.Combine(_lib.Root, "files", "fake");
        for (var i = 0; i < 450; i++)
        {
            scanner.Entries.Add(FakeScanner.Entry(Path.Combine(folder, "sub", $"f{i}.png")));
        }

        var reports = new List<ScanProgress>();
        var result = await Service.ScanAsync(source.Id, new SyncProgress(reports.Add), Ct);

        Assert.Equal(450, result.Added);
        Assert.Equal([200, 400], reports.Select(r => r.Found));
        Assert.All(reports, r => Assert.Equal(Path.Combine(folder, "sub"), r.CurrentDirectory));
    }

    [Fact]
    public async Task LargeScanIsCommittedInBatches()
    {
        var (source, scanner) = await FakeSourceAsync();
        var folder = Path.Combine(_lib.Root, "files", "fake");
        for (var i = 0; i < 2500; i++)
        {
            scanner.Entries.Add(FakeScanner.Entry(Path.Combine(folder, $"d{i % 7}", $"f{i}.png"), size: i + 1));
        }

        Assert.Equal(new ScanResult(2500, 0, 0, 0, 2500), await Service.ScanAsync(source.Id, null, Ct));
        Assert.Equal(new ScanResult(0, 0, 0, 0, 2500), await Service.ScanAsync(source.Id, null, Ct));
        for (var i = 0; i < 1500; i++)
        {
            scanner.Entries[i] = scanner.Entries[i] with { Size = scanner.Entries[i].Size + 1000 };
        }

        Assert.Equal(1500, (await Service.ScanAsync(source.Id, null, Ct)).Updated);
    }

    private sealed class SyncProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
