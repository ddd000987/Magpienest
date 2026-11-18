using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>Arguments of <see cref="BrowserViewModel.ItemsReplaced"/>.</summary>
/// <param name="Items">The new tile list.</param>
/// <param name="Selected">Tiles to select (kept by id across the refresh).</param>
/// <param name="PreserveScroll">Keep the scroll position (refresh after a change) instead of scrolling to the top (new query).</param>
internal sealed record ItemsReplacedArgs(IReadOnlyList<TileViewModel> Items, IReadOnlyList<TileViewModel> Selected, bool PreserveScroll);

/// <summary>A tag in the filter flyout.</summary>
internal sealed partial class TagFilterItem(string name, int count, bool isChecked, Action<TagFilterItem> changed) : ObservableObject
{
    public string Name { get; } = name;

    public int Count { get; } = count;

    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed(this);
}

/// <summary>
/// State and logic of one library browser (toolbar, folder tree, grid, details, empty states).
/// Lives on the UI thread. Item queries run in the library service, off the UI thread.
/// </summary>
internal sealed partial class BrowserViewModel : ObservableObject, IDisposable
{
    public const double MinTileWidth = 120;
    public const double MaxTileWidth = 360;
    public const double DefaultTileWidth = 180;
    public const double MinFolderPaneWidth = 160;
    public const double MaxFolderPaneWidth = 640;
    public const double DefaultFolderPaneWidth = 240;

    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ChangeDelay = TimeSpan.FromMilliseconds(300);

    private readonly BrowserServices _services;
    private readonly IPluginContext _owner;
    private readonly Debouncer _searchDebounce;
    private readonly Debouncer _changeDebounce;
    private readonly HashSet<string> _selectedTags = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<long, TileViewModel> _tilesById = [];
    private HashSet<long> _selectedIds = [];
    private IReadOnlyList<LibrarySource> _sources = [];
    private IReadOnlyList<string> _directories = [];
    private IReadOnlyList<string> _tagNames = [];
    private IReadOnlyList<FolderLabels> _folderLabels = [];

    /// <summary>The sources and folders the current tree was built from (null = no tree).</summary>
    private string? _treeShape;
    private CancellationTokenSource? _query;
    private int _refreshVersion;
    private bool _restoring;
    private bool _loaded;
    private bool _disposed;

    public BrowserViewModel(BrowserServices services, IPluginContext owner, BrowserOptions options)
    {
        _services = services;
        _owner = owner;
        Options = options;
        _searchDebounce = new Debouncer(SearchDelay, services.Delay, services.Log);
        _changeDebounce = new Debouncer(ChangeDelay, services.Delay, services.Log);
        Details = new DetailsViewModel(services, options);

        var sorts = options.Sorts.Count > 0 ? options.Sorts : [options.DefaultSort];
        SortOptions = [.. sorts.Distinct().Select(s => new LocalizedOption<ItemSort>(s, $"Library.Sort.{s}"))];
        RatingOptions = [new(0, "Library.Filter.RatingAny"), .. Enumerable.Range(1, 5).Select(n => new LocalizedOption<int>(n, "Library.Filter.RatingAtLeast", n))];
        StatusOptions = [new(null, "Library.Filter.StatusAny"), .. Enum.GetValues<ItemStatus>().Select(s => new LocalizedOption<ItemStatus?>(s, $"Library.Status.{s}"))];

        _restoring = true;
        TileWidth = Math.Clamp(Config("tileWidth", DefaultTileWidth), MinTileWidth, MaxTileWidth);
        FolderPaneWidth = Math.Clamp(Config("folderPaneWidth", DefaultFolderPaneWidth), MinFolderPaneWidth, MaxFolderPaneWidth);
        var storedSort = Config("sort", options.DefaultSort.ToString());
        SelectedSort = SortOptions.FirstOrDefault(o => o.Value.ToString() == storedSort)
                       ?? SortOptions.FirstOrDefault(o => o.Value == options.DefaultSort)
                       ?? SortOptions[0];
        Descending = Config("descending", false);
        IsDetailsVisible = Config("details", false);
        IsFolderTreeVisible = Config("folderTree", true);
        MinRating = RatingOptions[0];
        StatusFilter = StatusOptions[0];
        _restoring = false;

        LocSource.Instance.PropertyChanged += OnLanguageChanged;
        Initialization = InitializeAsync();
    }

    /// <summary>Raised after <see cref="Items"/> was replaced; the view applies selection and scroll.</summary>
    public event Action<ItemsReplacedArgs>? ItemsReplaced;

    public BrowserOptions Options { get; }

    public string Kind => Options.Kind;

    public DetailsViewModel Details { get; }

    /// <summary>Completes after the first load.</summary>
    public Task Initialization { get; }

    /// <summary>The most recent item refresh (tests await it).</summary>
    public Task LastRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>The most recent debounced reload or search (tests await it).</summary>
    public Task PendingDebounce => Task.WhenAll(_searchDebounce.Last, _changeDebounce.Last);

    public IReadOnlyList<TileViewModel> Items { get; private set; } = [];

    public IReadOnlyList<TileViewModel> SelectedTiles { get; private set; } = [];

    public IReadOnlyList<LocalizedOption<ItemSort>> SortOptions { get; }

    public IReadOnlyList<LocalizedOption<int>> RatingOptions { get; }

    public IReadOnlyList<LocalizedOption<ItemStatus?>> StatusOptions { get; }

    public IReadOnlyList<FolderNode> FolderRoots { get; private set; } = [];

    public IReadOnlyList<TagFilterItem> TagFilters { get; private set; } = [];

    public bool HasTagFilters => TagFilters.Count > 0;

    // ---- toolbar state -------------------------------------------------------------------------

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial LocalizedOption<ItemSort> SelectedSort { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectionGlyph), nameof(DirectionToolTip))]
    public partial bool Descending { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterActive))]
    public partial bool FavoritesOnly { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterActive))]
    public partial LocalizedOption<int> MinRating { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterActive))]
    public partial LocalizedOption<ItemStatus?> StatusFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterActive))]
    public partial bool ShowMissing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThumbnailWidth), nameof(ThumbnailHeight), nameof(CellWidth), nameof(CellHeight), nameof(CellSize))]
    public partial double TileWidth { get; set; }

    [ObservableProperty]
    public partial bool IsDetailsVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFolderPane))]
    public partial bool IsFolderTreeVisible { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand), nameof(AddFolderCommand))]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    public partial FolderNode? SelectedFolder { get; private set; }

    // ---- derived -------------------------------------------------------------------------------

    public string DirectionGlyph => Descending ? "" : "";

    public string DirectionToolTip => Loc.Get(Descending ? "Library.Browser.Descending" : "Library.Browser.Ascending");

    public bool HasScanner => _services.Library.Kinds.FirstOrDefault(k => k.Id == Kind) is not { Scanner: null };

    public bool ShowFolderTreeOption => Options.ShowFolderTree && HasScanner;

    public bool ShowFolderPane => ShowFolderTreeOption && IsFolderTreeVisible;

    public bool ShowFoldersButton => HasScanner;

    public bool CanAddItem => Options.AddItem is not null;

    public bool ShowStatus => Options.ShowStatus;

    public Stretch ThumbnailStretch => Options.ThumbnailStretch;

    /// <summary>Tiles show their title as a tooltip unless something else follows the mouse on them (hover preview).</summary>
    public bool ShowTileToolTip => Options.TileHovered is null;

    public double ThumbnailWidth => TileWidth;

    public double ThumbnailHeight => Math.Round(TileWidth / (Options.TileAspectRatio > 0 ? Options.TileAspectRatio : 1.0));

    /// <summary>Container width: tile plus 8 px padding on each side.</summary>
    public double CellWidth => TileWidth + 16;

    /// <summary>Container height: padding, thumbnail, title line and optional subtitle line.</summary>
    public double CellHeight => 8 + ThumbnailHeight + 26 + (Options.Subtitle is null ? 0 : 18) + 8;

    public System.Windows.Size CellSize => new(CellWidth, CellHeight);

    public bool IsFilterActive =>
        FavoritesOnly || MinRating.Value > 0 || (ShowStatus && StatusFilter.Value is not null) || _selectedTags.Count > 0;

    /// <summary>Any condition that can make the result empty, including the search box and the folder.</summary>
    public bool IsQueryNarrowed => IsFilterActive || !string.IsNullOrWhiteSpace(SearchText) || SelectedFolder is { IsRoot: false };

    public bool ShowEmptyNoSources => _loaded && Items.Count == 0 && HasScanner && _sources.Count == 0;

    public bool ShowEmptyManual => _loaded && Items.Count == 0 && !HasScanner && !IsQueryNarrowed;

    public bool ShowEmptyNoMatch => _loaded && Items.Count == 0 && !ShowEmptyNoSources && !ShowEmptyManual;

    public string StatusText
    {
        get
        {
            var count = Items.Count.ToString("N0", CultureInfo.CurrentCulture);
            return SelectedTiles.Count == 0
                ? Loc.Format("Library.Browser.Status.Items", count)
                : Loc.Format("Library.Browser.Status.ItemsSelected", count, SelectedTiles.Count.ToString("N0", CultureInfo.CurrentCulture));
        }
    }

    private string KindName =>
        _services.Library.Kinds.FirstOrDefault(k => k.Id == Kind) is { } kind ? Loc.Get(kind.DisplayNameKey) : Kind;

    // ---- query ---------------------------------------------------------------------------------

    /// <summary>The query that the current toolbar, filter and folder state describe.</summary>
    public ItemQuery BuildQuery()
    {
        var folder = SelectedFolder;
        return new ItemQuery
        {
            Kind = Kind,
            Text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            SourceId = folder?.SourceId,
            Directory = folder is { IsRoot: false, SourceId: null } ? folder.Path : null,
            IncludeSubdirectories = true,
            Tags = [.. _selectedTags.Order(LibraryRules.Natural)],
            FavoritesOnly = FavoritesOnly,
            MinRating = MinRating.Value,
            Status = ShowStatus ? StatusFilter.Value : null,
            IncludeMissing = ShowMissing,
            Sort = SelectedSort.Value,
            Descending = Descending,
        };
    }

    /// <summary>Called by the library-changed fan-out (UI thread).</summary>
    public void OnLibraryChanged(LibraryChanged change)
    {
        if (!_disposed && string.Equals(change.Kind, Kind, StringComparison.Ordinal))
        {
            _changeDebounce.Schedule(ReloadAllAsync);
        }
    }

    /// <summary>Called by the view whenever the grid selection changed.</summary>
    public void SetSelection(IReadOnlyList<TileViewModel> selected)
    {
        SelectedTiles = selected;
        _selectedIds = [.. selected.Select(t => t.Id)];
        OnPropertyChanged(nameof(StatusText));
        Details.Show([.. selected.Select(t => t.Item)], _tagNames);
    }

    /// <summary>Runs the query and replaces <see cref="Items"/> wholesale.</summary>
    public Task RefreshAsync(bool preserveScroll)
    {
        LastRefresh = RefreshCoreAsync(preserveScroll);
        return LastRefresh;
    }

    /// <summary>Reloads sources, folder tree and tags, then the items (keeping scroll and selection).</summary>
    public async Task ReloadAllAsync()
    {
        await ReloadMetaAsync();
        await RefreshAsync(preserveScroll: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        LocSource.Instance.PropertyChanged -= OnLanguageChanged;
        _searchDebounce.Dispose();
        _changeDebounce.Dispose();
        _query?.Cancel();
        Details.Cancel();
        foreach (var tile in Items)
        {
            tile.Detach();
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            await ReloadAllAsync();
        }
        catch (Exception ex)
        {
            _services.Log.Error($"Loading the {Kind} browser failed.", ex);
        }
    }

    private async Task RefreshCoreAsync(bool preserveScroll)
    {
        var version = ++_refreshVersion;
        _query?.Cancel();
        var query = new CancellationTokenSource();
        _query = query;
        var request = BuildQuery();

        IReadOnlyList<LibraryItem> items;
        try
        {
            items = await _services.Library.QueryAsync(request, query.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _services.Log.Error($"The {Kind} query failed.", ex);
            return;
        }

        if (_disposed || version != _refreshVersion)
        {
            return;
        }

        var old = _tilesById;
        var tiles = new List<TileViewModel>(items.Count);
        var byId = new Dictionary<long, TileViewModel>(items.Count);
        foreach (var item in items)
        {
            if (old.TryGetValue(item.Id, out var tile))
            {
                tile.Update(item);
            }
            else
            {
                tile = new TileViewModel(item, _services.Thumbnails, Options);
            }

            tiles.Add(tile);
            byId[item.Id] = tile;
        }

        var selected = tiles.Where(t => _selectedIds.Contains(t.Id)).ToList();
        _tilesById = byId;
        Items = tiles;
        _loaded = true;
        OnPropertyChanged(nameof(Items));
        RaiseEmptyStates();
        ItemsReplaced?.Invoke(new ItemsReplacedArgs(tiles, selected, preserveScroll));
        SetSelection(selected);
    }

    private async Task ReloadMetaAsync()
    {
        var sources = await _services.Library.GetSourcesAsync(Kind);
        var directories = ShowFolderTreeOption ? await _services.Library.GetDirectoriesAsync(Kind) : [];
        var tags = await _services.Library.GetTagsAsync(Kind);
        var folderLabels = ShowFolderTreeOption ? await _services.Library.GetFolderLabelsAsync(Kind) : [];
        if (_disposed)
        {
            return;
        }

        _sources = sources;
        _directories = directories;
        _folderLabels = folderLabels;
        _tagNames = [.. tags.Select(t => t.Name)];
        RebuildTree();

        var tagsChanged = _selectedTags.RemoveWhere(name => !tags.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) > 0;
        TagFilters = [.. tags.Select(t => new TagFilterItem(t.Name, t.Count, _selectedTags.Contains(t.Name), OnTagFilterChanged))];
        OnPropertyChanged(nameof(TagFilters));
        OnPropertyChanged(nameof(HasTagFilters));
        OnPropertyChanged(nameof(HasScanner));
        OnPropertyChanged(nameof(ShowFolderTreeOption));
        OnPropertyChanged(nameof(ShowFolderPane));
        OnPropertyChanged(nameof(ShowFoldersButton));
        RescanCommand.NotifyCanExecuteChanged();
        AddFolderCommand.NotifyCanExecuteChanged();
        if (tagsChanged)
        {
            OnPropertyChanged(nameof(IsFilterActive));
        }
    }

    private void RebuildTree()
    {
        if (!ShowFolderTreeOption)
        {
            _treeShape = null;
            FolderRoots = [];
            SelectedFolder = null;
            OnPropertyChanged(nameof(FolderRoots));
            return;
        }

        var rootName = Loc.Get("Library.Browser.AllFolders");
        var shape = string.Join('\n', [rootName, .. _sources.Select(s => $"{s.Id}|{s.Path}"), "", .. _directories]);
        if (FolderRoots.FirstOrDefault() is { } current && string.Equals(shape, _treeShape, StringComparison.Ordinal))
        {
            // Same folders: update the labels in place, so the TreeView keeps its containers and its selection.
            ApplyLabels([.. current.SelfAndDescendants()]);
            return;
        }

        var previous = FolderRoots.FirstOrDefault();
        var expanded = previous?.SelfAndDescendants()
            .ToDictionary(n => n.Key, n => n.IsExpanded, StringComparer.OrdinalIgnoreCase);
        var selectedKey = SelectedFolder?.Key ?? "";

        var root = FolderTreeBuilder.Build(rootName, _sources, _directories, expanded);
        var nodes = root.SelfAndDescendants().ToList();
        ApplyLabels(nodes);
        var selected = nodes.FirstOrDefault(n => string.Equals(n.Key, selectedKey, StringComparison.OrdinalIgnoreCase)) ?? root;

        // A TreeView whose items are replaced selects its first item (the root) by itself; that selection must not
        // reach the query, and the nodes must end up with only the kept folder selected.
        _restoring = true;
        try
        {
            if (previous is not null)
            {
                foreach (var node in previous.SelfAndDescendants())
                {
                    node.Selected = null;
                }
            }

            selected.IsSelected = true;
            foreach (var node in nodes)
            {
                node.Selected = OnFolderSelected;
            }

            SelectedFolder = selected;
            _treeShape = shape;
            FolderRoots = [root];
            OnPropertyChanged(nameof(FolderRoots));
            foreach (var node in nodes)
            {
                node.IsSelected = ReferenceEquals(node, selected);
            }
        }
        finally
        {
            _restoring = false;
        }
    }

    private void ApplyLabels(IReadOnlyList<FolderNode> nodes)
    {
        var labels = _folderLabels.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (node.Path is { } path && labels.TryGetValue(path, out var label))
            {
                node.SetLabels(label.Rating, label.Tags);
            }
            else
            {
                node.SetLabels(0, []);
            }
        }
    }

    private void OnFolderSelected(FolderNode node)
    {
        if (_restoring || ReferenceEquals(node, SelectedFolder))
        {
            return;
        }

        SelectedFolder = node;
        ApplySortFor(node);
        QueryChanged();
    }

    private void OnTagFilterChanged(TagFilterItem item)
    {
        var changed = item.IsChecked ? _selectedTags.Add(item.Name) : _selectedTags.Remove(item.Name);
        if (changed)
        {
            OnPropertyChanged(nameof(IsFilterActive));
            QueryChanged();
        }
    }

    private void QueryChanged()
    {
        if (!_restoring && !_disposed)
        {
            _ = RefreshAsync(preserveScroll: false);
        }
    }

    private void RaiseEmptyStates()
    {
        OnPropertyChanged(nameof(ShowEmptyNoSources));
        OnPropertyChanged(nameof(ShowEmptyManual));
        OnPropertyChanged(nameof(ShowEmptyNoMatch));
        OnPropertyChanged(nameof(IsQueryNarrowed));
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnSearchTextChanged(string value)
    {
        if (!_restoring && !_disposed)
        {
            _searchDebounce.Schedule(() => RefreshAsync(preserveScroll: false));
        }
    }

    partial void OnSelectedSortChanged(LocalizedOption<ItemSort> value)
    {
        if (!_restoring)
        {
            RememberSort();
        }

        QueryChanged();
    }

    partial void OnDescendingChanged(bool value)
    {
        if (!_restoring)
        {
            RememberSort();
        }

        QueryChanged();
    }

    partial void OnFavoritesOnlyChanged(bool value) => QueryChanged();

    partial void OnMinRatingChanged(LocalizedOption<int> value) => QueryChanged();

    partial void OnStatusFilterChanged(LocalizedOption<ItemStatus?> value) => QueryChanged();

    partial void OnShowMissingChanged(bool value) => QueryChanged();

    /// <summary>Width of the folder pane, dragged by its edge.</summary>
    [ObservableProperty]
    public partial double FolderPaneWidth { get; private set; }

    /// <summary>Sets the folder pane's width (clamped); <paramref name="save"/> stores it (at the end of a drag).</summary>
    public void ResizeFolderPane(double width, bool save)
    {
        FolderPaneWidth = Math.Round(Math.Clamp(width, MinFolderPaneWidth, MaxFolderPaneWidth));
        if (save)
        {
            SetConfig("folderPaneWidth", FolderPaneWidth);
        }
    }

    partial void OnTileWidthChanged(double value)
    {
        if (!_restoring)
        {
            SetConfig("tileWidth", value);
        }
    }

    partial void OnIsDetailsVisibleChanged(bool value)
    {
        if (!_restoring)
        {
            SetConfig("details", value);
        }
    }

    partial void OnIsFolderTreeVisibleChanged(bool value)
    {
        if (!_restoring)
        {
            SetConfig("folderTree", value);
        }
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        foreach (var option in SortOptions)
        {
            option.Refresh();
        }

        foreach (var option in RatingOptions)
        {
            option.Refresh();
        }

        foreach (var option in StatusOptions)
        {
            option.Refresh();
        }

        Details.RefreshTexts();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DirectionToolTip));
        RebuildTree();
    }

    private T Config<T>(string name, T defaultValue)
    {
        try
        {
            return _services.Config.Get($"browser.{Kind}.{name}", defaultValue);
        }
        catch (Exception)
        {
            return defaultValue;
        }
    }

    private void SetConfig<T>(string name, T value) => _services.Config.Set($"browser.{Kind}.{name}", value);

    // ---- commands ------------------------------------------------------------------------------

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    [RelayCommand]
    private void ToggleDescending() => Descending = !Descending;

    [RelayCommand]
    private void ClearFilters()
    {
        _restoring = true;
        try
        {
            SearchText = "";
            FavoritesOnly = false;
            MinRating = RatingOptions[0];
            StatusFilter = StatusOptions[0];
            _selectedTags.Clear();
            foreach (var tag in TagFilters)
            {
                tag.IsChecked = false;
            }

            if (FolderRoots.FirstOrDefault() is { } root)
            {
                root.IsSelected = true;
                SelectedFolder = root;
                ApplySortFor(root);
            }
        }
        finally
        {
            _restoring = false;
        }

        _searchDebounce.Cancel();
        OnPropertyChanged(nameof(IsFilterActive));
        QueryChanged();
    }
}
