using Magpienest.Plugins.Library.Browser;
using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.Library.Tests;

public sealed class FolderTreeBuilderTests
{
    private static readonly IReadOnlyList<LibrarySource> Sources =
    [
        new(1, "test", @"D:\Pics", DateTimeOffset.UnixEpoch, null, 0),
        new(2, "test", @"E:\", DateTimeOffset.UnixEpoch, null, 0),
    ];

    private static readonly IReadOnlyList<string> Directories =
    [
        @"D:\Pics",
        @"D:\Pics\2024\01",
        @"D:\Pics\日本語",
        @"D:\Pics\10",
        @"D:\Pics\2",
        @"E:\Art",
        @"F:\Elsewhere",
    ];

    [Fact]
    public void BuildsNestedNaturallySortedNodesUnderTheirSource()
    {
        var root = FolderTreeBuilder.Build("All", Sources, Directories, expansion: null);

        Assert.True(root.IsRoot);
        Assert.Equal(["E:\\", "Pics"], root.Children.Select(c => c.Name));
        var pics = root.Children[1];
        Assert.Equal(1, pics.SourceId);
        Assert.Equal(["2", "10", "2024", "日本語"], pics.Children.Select(c => c.Name));
        var year = pics.Children[2];
        Assert.Equal(@"D:\Pics\2024", year.Path);
        Assert.Null(year.SourceId);
        Assert.Equal(@"D:\Pics\2024\01", Assert.Single(year.Children).Path);
        Assert.Equal(@"E:\Art", Assert.Single(root.Children[0].Children).Path);
        Assert.DoesNotContain(root.SelfAndDescendants(), n => n.Path?.StartsWith('F') == true);
    }

    [Fact]
    public void FirstBuildExpandsSourcesThenKeepsExpansionByPath()
    {
        var first = FolderTreeBuilder.Build("All", Sources, Directories, expansion: null);
        Assert.All(first.Children, c => Assert.True(c.IsExpanded));
        Assert.All(first.Children[1].Children, c => Assert.False(c.IsExpanded));

        var expansion = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [@"d:\pics"] = false,
            [@"d:\pics\2024"] = true,
        };
        var second = FolderTreeBuilder.Build("All", Sources, Directories, expansion);
        Assert.True(second.IsExpanded);
        Assert.False(second.Children[1].IsExpanded);
        Assert.True(second.Children[1].Children.Single(c => c.Name == "2024").IsExpanded);
        Assert.True(second.Children[0].IsExpanded); // E:\ was not in the previous tree: a new source opens by default
        Assert.False(second.Children[1].Children.Single(c => c.Name == "10").IsExpanded);
    }
}

public sealed class BrowserViewModelTests
{
    private const string Kind = "test";

    [Fact]
    public async Task QueryReflectsToolbarFilterAndFolderState()
    {
        using var lib = new TestLibrary();
        var folder = lib.NewFolder("pics");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        var scanner = new FakeScanner();
        scanner.Entries.Add(FakeScanner.Entry(Path.Combine(folder, "a.png")));
        scanner.Entries.Add(FakeScanner.Entry(Path.Combine(folder, "sub", "b.png")));
        lib.RegisterScanner(Kind, scanner);
        var source = await lib.Service.AddSourceAsync(Kind, folder);
        await lib.Service.ScanAsync(source.Id, ct: TestContext.Current.CancellationToken);
        var item = (await lib.Service.QueryAsync(new ItemQuery { Kind = Kind }, TestContext.Current.CancellationToken))[0];
        await lib.Service.SetTagsAsync(item.Id, ["red", "blue"]);

        using var vm = new BrowserViewModel(BrowserHarness.Services(lib.Service), lib.Context, BrowserHarness.Options());
        await vm.Initialization;

        var initial = vm.BuildQuery();
        Assert.Equal(Kind, initial.Kind);
        Assert.Null(initial.Text);
        Assert.Null(initial.SourceId);
        Assert.Null(initial.Directory);
        Assert.Equal(ItemSort.Title, initial.Sort);
        Assert.False(initial.Descending);

        vm.SearchText = "  cat  ";
        vm.FavoritesOnly = true;
        vm.MinRating = vm.RatingOptions[3];
        vm.StatusFilter = vm.StatusOptions[2];
        vm.ShowMissing = true;
        vm.SelectedSort = vm.SortOptions.Single(o => o.Value == ItemSort.Rating);
        vm.Descending = true;
        vm.TagFilters.Single(t => t.Name == "red").IsChecked = true;
        vm.TagFilters.Single(t => t.Name == "blue").IsChecked = true;

        var query = vm.BuildQuery();
        Assert.Equal("cat", query.Text);
        Assert.True(query.FavoritesOnly);
        Assert.Equal(3, query.MinRating);
        Assert.Null(query.Status); // the kind does not show status
        Assert.True(query.IncludeMissing);
        Assert.Equal(ItemSort.Rating, query.Sort);
        Assert.True(query.Descending);
        Assert.Equal(["blue", "red"], query.Tags);
        Assert.True(vm.IsFilterActive);

        var sourceNode = vm.FolderRoots[0].Children.Single();
        sourceNode.IsSelected = true;
        Assert.Equal(source.Id, vm.BuildQuery().SourceId);
        Assert.Null(vm.BuildQuery().Directory);

        var subNode = sourceNode.Children.Single();
        subNode.IsSelected = true;
        Assert.Null(vm.BuildQuery().SourceId);
        Assert.Equal(Path.Combine(source.Path, "sub"), vm.BuildQuery().Directory);
        Assert.True(vm.BuildQuery().IncludeSubdirectories);

        vm.ClearFiltersCommand.Execute(null);
        await vm.LastRefresh;
        var cleared = vm.BuildQuery();
        Assert.Null(cleared.Text);
        Assert.False(cleared.FavoritesOnly);
        Assert.Equal(0, cleared.MinRating);
        Assert.Empty(cleared.Tags);
        Assert.Null(cleared.SourceId);
        Assert.Null(cleared.Directory);
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public async Task FolderLabelsShowOnTheTreeAndInTheTagFilters()
    {
        using var lib = new TestLibrary();
        var folder = lib.NewFolder("pics");
        var scanner = new FakeScanner();
        scanner.Entries.Add(FakeScanner.Entry(Path.Combine(folder, "a.png")));
        scanner.Entries.Add(FakeScanner.Entry(Path.Combine(folder, "sub", "b.png")));
        lib.RegisterScanner(Kind, scanner);
        var source = await lib.Service.AddSourceAsync(Kind, folder);
        await lib.Service.ScanAsync(source.Id, ct: TestContext.Current.CancellationToken);
        await lib.Service.SetFolderLabelsAsync(Kind, Path.Combine(source.Path, "sub"), 4, ["night"]);

        using var vm = new BrowserViewModel(BrowserHarness.Services(lib.Service), lib.Context, BrowserHarness.Options());
        await vm.Initialization;

        var sourceNode = vm.FolderRoots[0].Children.Single();
        var subNode = sourceNode.Children.Single();
        Assert.False(sourceNode.HasRating || sourceNode.HasTags);
        Assert.Equal(4, subNode.Rating);
        Assert.Equal(["night"], subNode.Tags);
        Assert.Equal(1, vm.TagFilters.Single(t => t.Name == "night").Count);
    }

    [Fact]
    public async Task StatusFilterAppliesOnlyWhenTheKindShowsStatus()
    {
        using var lib = new TestLibrary();
        using var vm = new BrowserViewModel(BrowserHarness.Services(lib.Service), lib.Context, BrowserHarness.Options(showStatus: true));
        await vm.Initialization;

        vm.StatusFilter = vm.StatusOptions.Single(o => o.Value == ItemStatus.Completed);

        Assert.Equal(ItemStatus.Completed, vm.BuildQuery().Status);
        Assert.True(vm.IsFilterActive);
    }

    [Fact]
    public async Task SortAndTileWidthArePersistedPerKind()
    {
        using var lib = new TestLibrary();
        var services = BrowserHarness.Services(lib.Service);
        using (var vm = new BrowserViewModel(services, lib.Context, BrowserHarness.Options()))
        {
            await vm.Initialization;
            vm.SelectedSort = vm.SortOptions.Single(o => o.Value == ItemSort.AddedAt);
            vm.Descending = true;
            vm.TileWidth = 250;
            Assert.False(vm.IsDetailsVisible);
            vm.IsDetailsVisible = true;
            vm.ResizeFolderPane(9999, save: true);
            await vm.LastRefresh;
        }

        using var again = new BrowserViewModel(services, lib.Context, BrowserHarness.Options());
        await again.Initialization;
        Assert.Equal(ItemSort.AddedAt, again.SelectedSort.Value);
        Assert.True(again.Descending);
        Assert.Equal(250, again.TileWidth);
        Assert.True(again.IsDetailsVisible);
        Assert.Equal(BrowserViewModel.MaxFolderPaneWidth, again.FolderPaneWidth);

        using var other = new BrowserViewModel(services, lib.Context, BrowserHarness.Options(kind: "other"));
        await other.Initialization;
        Assert.Equal(ItemSort.Title, other.SelectedSort.Value);
        Assert.Equal(BrowserViewModel.DefaultTileWidth, other.TileWidth);
    }

    [Fact]
    public async Task SelectionSurvivesARefreshByIdAndDropsRemovedItems()
    {
        using var lib = new TestLibrary();
        var a = await lib.AddAsync(Kind, @"C:\x\a.png");
        var b = await lib.AddAsync(Kind, @"C:\x\b.png");
        var c = await lib.AddAsync(Kind, @"C:\x\c.png");
        using var vm = new BrowserViewModel(BrowserHarness.Services(lib.Service), lib.Context, BrowserHarness.Options());
        await vm.Initialization;
        var replaced = new List<ItemsReplacedArgs>();
        vm.ItemsReplaced += replaced.Add;

        var tileA = vm.Items.Single(t => t.Id == a.Id);
        var tileC = vm.Items.Single(t => t.Id == c.Id);
        vm.SetSelection([tileA, tileC]);

        await lib.Service.UpdateAsync(a.Id, new ItemPatch { Title = "renamed", Rating = 4 });
        vm.OnLibraryChanged(lib.Events[^1]);
        await vm.PendingDebounce;

        var args = Assert.Single(replaced);
        Assert.True(args.PreserveScroll);
        Assert.Equal([a.Id, c.Id], args.Selected.Select(t => t.Id).Order());
        Assert.Same(tileA, vm.Items.Single(t => t.Id == a.Id)); // tile reused, not recreated
        Assert.Equal("renamed", tileA.Title);
        Assert.Equal([a.Id, c.Id], vm.SelectedTiles.Select(t => t.Id).Order());

        await lib.Service.RemoveAsync([c.Id]);
        vm.OnLibraryChanged(new LibraryChanged(Kind, LibraryChangeType.Removed, [c.Id]));
        await vm.PendingDebounce;

        Assert.Equal([a.Id, b.Id], vm.Items.Select(t => t.Id).Order());
        Assert.Equal([a.Id], vm.SelectedTiles.Select(t => t.Id));
    }

    [Fact]
    public async Task ANewQueryScrollsToTheTop()
    {
        using var lib = new TestLibrary();
        await lib.AddAsync(Kind, @"C:\x\a.png");
        using var vm = new BrowserViewModel(BrowserHarness.Services(lib.Service), lib.Context, BrowserHarness.Options());
        await vm.Initialization;
        var replaced = new List<ItemsReplacedArgs>();
        vm.ItemsReplaced += replaced.Add;

        vm.Descending = true;
        await vm.LastRefresh;

        Assert.False(Assert.Single(replaced).PreserveScroll);
    }

    [Fact]
    public async Task LibraryChangesAreDebouncedIntoOneReload()
    {
        using var lib = new TestLibrary();
        await lib.AddAsync(Kind, @"C:\x\a.png");
        var delay = new ManualDelay();
        using var vm = new BrowserViewModel(BrowserHarness.Services(lib.Service, delay.Wait), lib.Context, BrowserHarness.Options());
        await vm.Initialization;
        var replaced = 0;
        vm.ItemsReplaced += _ => replaced++;

        vm.OnLibraryChanged(new LibraryChanged(Kind, LibraryChangeType.Updated, [1]));
        vm.OnLibraryChanged(new LibraryChanged(Kind, LibraryChangeType.Updated, [1]));
        vm.OnLibraryChanged(new LibraryChanged("another-kind", LibraryChangeType.Reset, []));
        vm.OnLibraryChanged(new LibraryChanged(Kind, LibraryChangeType.Reset, []));
        Assert.Equal(3, delay.Count); // the other kind is ignored
        Assert.Equal(0, replaced);

        delay.ReleaseAll();
        await vm.PendingDebounce;

        Assert.Equal(1, replaced);
    }

    [Fact]
    public async Task SearchTypingIsDebounced()
    {
        using var lib = new TestLibrary();
        await lib.AddAsync(Kind, @"C:\x\apple.png");
        await lib.AddAsync(Kind, @"C:\x\banana.png");
        var delay = new ManualDelay();
        using var vm = new BrowserViewModel(BrowserHarness.Services(lib.Service, delay.Wait), lib.Context, BrowserHarness.Options());
        await vm.Initialization;
        var replaced = 0;
        vm.ItemsReplaced += _ => replaced++;

        vm.SearchText = "b";
        vm.SearchText = "ba";
        vm.SearchText = "ban";
        Assert.Equal(0, replaced);

        delay.ReleaseAll();
        await vm.PendingDebounce;

        Assert.Equal(1, replaced);
        Assert.Equal("banana", Assert.Single(vm.Items).Title);
    }

    [Fact]
    public async Task DebouncerRunsOnlyTheLastAction()
    {
        var delay = new ManualDelay();
        using var debouncer = new Debouncer(TimeSpan.FromSeconds(1), delay.Wait);
        var runs = new List<int>();

        for (var i = 1; i <= 3; i++)
        {
            var n = i;
            debouncer.Schedule(() =>
            {
                runs.Add(n);
                return Task.CompletedTask;
            });
        }

        delay.ReleaseAll();
        await debouncer.Last;

        Assert.Equal([3], runs);
    }

    [Fact]
    public async Task EmptyStatesFollowSourcesAndFilters()
    {
        using var lib = new TestLibrary();
        lib.RegisterScanner(Kind, new FakeScanner());
        using var vm = new BrowserViewModel(BrowserHarness.Services(lib.Service), lib.Context, BrowserHarness.Options());
        await vm.Initialization;
        Assert.True(vm.ShowEmptyNoSources);
        Assert.False(vm.ShowEmptyNoMatch);

        var folder = lib.NewFolder("empty");
        await lib.Service.AddSourceAsync(Kind, folder);
        await vm.ReloadAllAsync();
        Assert.False(vm.ShowEmptyNoSources);
        Assert.True(vm.ShowEmptyNoMatch);
        Assert.False(vm.IsQueryNarrowed);

        lib.Service.RegisterKind(lib.Context, new ItemKindDescriptor { Id = "manual", DisplayNameKey = "Test.Manual" });
        using var manual = new BrowserViewModel(BrowserHarness.Services(lib.Service), lib.Context, BrowserHarness.Options(kind: "manual"));
        await manual.Initialization;
        Assert.True(manual.ShowEmptyManual);
        manual.FavoritesOnly = true;
        await manual.LastRefresh;
        Assert.False(manual.ShowEmptyManual);
        Assert.True(manual.ShowEmptyNoMatch);
    }

    [Fact]
    public async Task RemoveSelectedAsksAndRemovesOnlyFromTheDatabase()
    {
        using var lib = new TestLibrary();
        var file = Path.Combine(lib.NewFolder("keep"), "keep.png");
        File.WriteAllBytes(file, [1, 2, 3]);
        var item = await lib.AddAsync(Kind, file);
        var shell = new FakeShell { ConfirmResult = false };
        using var vm = new BrowserViewModel(BrowserHarness.Services(lib.Service, shell: shell), lib.Context, BrowserHarness.Options());
        await vm.Initialization;
        vm.SetSelection([vm.Items.Single()]);

        await vm.RemoveSelectedCommand.ExecuteAsync(null);
        Assert.NotNull(await lib.Service.GetAsync(item.Id));

        shell.ConfirmResult = true;
        await vm.RemoveSelectedCommand.ExecuteAsync(null);
        Assert.Null(await lib.Service.GetAsync(item.Id));
        Assert.True(File.Exists(file));
    }
}
