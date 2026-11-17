using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>The details panel. Single selection edits one item; multiple selection offers bulk actions (on the browser).</summary>
internal sealed partial class DetailsViewModel : ObservableObject
{
    private readonly BrowserServices _services;
    private readonly BrowserOptions _options;
    private CancellationTokenSource? _thumbnailLoad;
    private bool _loading;

    public DetailsViewModel(BrowserServices services, BrowserOptions options)
    {
        _services = services;
        _options = options;
        Tags.Changed += () => _ = SaveTagsAsync();
    }

    public LibraryItem? Item { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNone), nameof(IsSingle), nameof(IsMultiple))]
    public partial int Count { get; private set; }

    public bool IsNone => Count == 0;

    public bool IsSingle => Count == 1;

    public bool IsMultiple => Count > 1;

    public string CountText => Loc.Format("Library.Details.SelectedCount", Count.ToString("N0", CultureInfo.CurrentCulture));

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Notes { get; set; } = "";

    [ObservableProperty]
    public partial int Rating { get; set; }

    [ObservableProperty]
    public partial bool IsFavorite { get; set; }

    [ObservableProperty]
    public partial LocalizedOption<ItemStatus>? Status { get; set; }

    public IReadOnlyList<LocalizedOption<ItemStatus>> StatusOptions { get; } =
        [.. Enum.GetValues<ItemStatus>().Select(s => new LocalizedOption<ItemStatus>(s, $"Library.Status.{s}"))];

    public bool ShowStatus => _options.ShowStatus;

    public TagEditorViewModel Tags { get; } = new();

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; private set; }

    [ObservableProperty]
    public partial FrameworkElement? CustomDetails { get; private set; }

    public string PathText => Item?.Path ?? "";

    public string SizeText => Item is null ? "" : FormatSize(Item.FileSize);

    public string ModifiedText => Item is null ? "" : FormatDate(Item.FileModified);

    public string AddedText => Item is null ? "" : FormatDate(Item.AddedAt);

    public string OpenedText => Item?.LastOpenedAt is { } opened ? FormatDate(opened) : Loc.Get("Library.Details.Never");

    public bool IsMissing => Item?.IsMissing == true;

    /// <summary>The rating the item gets from its folders, shown while it has none of its own.</summary>
    public string FolderRatingText => Item is { Rating: 0, FolderRating: > 0 } item ? Loc.Format("Library.Details.FolderRating", item.FolderRating) : "";

    public bool HasFolderRating => FolderRatingText.Length > 0;

    public string FolderTagsText => Item is { FolderTags.Count: > 0 } item
        ? Loc.Format("Library.Details.FolderTags", string.Join(Loc.Get("Library.Folder.TagSeparator"), item.FolderTags))
        : "";

    public bool HasFolderTags => FolderTagsText.Length > 0;

    /// <summary>Shows the current selection. Values the user is editing survive a refresh of the same item.</summary>
    public void Show(IReadOnlyList<LibraryItem> selection, IReadOnlyList<string> allTags)
    {
        Tags.Suggestions = allTags;
        var previous = Item;
        Count = selection.Count;
        OnPropertyChanged(nameof(CountText));
        if (selection.Count != 1)
        {
            Clear();
            return;
        }

        var item = selection[0];
        if (ReferenceEquals(previous, item))
        {
            return;
        }

        var sameItem = previous?.Id == item.Id;
        _loading = true;
        try
        {
            Item = item;
            if (!sameItem || Title == previous!.Title)
            {
                Title = item.Title;
            }

            if (!sameItem || Notes == previous!.Notes)
            {
                Notes = item.Notes;
            }

            Rating = item.Rating;
            IsFavorite = item.IsFavorite;
            Status = StatusOptions.First(o => o.Value == item.Status);
            Tags.Reset(item.Tags);
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ModifiedText));
        OnPropertyChanged(nameof(AddedText));
        OnPropertyChanged(nameof(OpenedText));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(FolderRatingText));
        OnPropertyChanged(nameof(HasFolderRating));
        OnPropertyChanged(nameof(FolderTagsText));
        OnPropertyChanged(nameof(HasFolderTags));
        CustomDetails = CreateCustomDetails(item);
        if (!sameItem || item.FileSize != previous!.FileSize || item.FileModified != previous.FileModified
            || item.CoverPath != previous.CoverPath)
        {
            _ = LoadThumbnailAsync(item);
        }
    }

    /// <summary>Re-reads localized texts.</summary>
    public void RefreshTexts()
    {
        foreach (var option in StatusOptions)
        {
            option.Refresh();
        }

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(OpenedText));
        OnPropertyChanged(nameof(SizeText));
    }

    public void Cancel()
    {
        _thumbnailLoad?.Cancel();
        _thumbnailLoad = null;
    }

    /// <summary>Commits the title (Enter or focus loss).</summary>
    public void CommitTitle()
    {
        if (Item is null || _loading)
        {
            return;
        }

        var title = (Title ?? "").Trim();
        if (title.Length == 0)
        {
            Title = Item.Title;
            return;
        }

        if (!string.Equals(title, Item.Title, StringComparison.Ordinal))
        {
            _ = PatchAsync(new ItemPatch { Title = title });
        }
    }

    [RelayCommand]
    private void SetRating(int value) => Rating = Rating == value ? 0 : value;

    [RelayCommand]
    private void ShowInExplorer()
    {
        if (Item is not null)
        {
            ExplorerLauncher.Show(Item.Path, _services.Log);
        }
    }

    partial void OnNotesChanged(string value)
    {
        if (!_loading && Item is not null && !string.Equals(value, Item.Notes, StringComparison.Ordinal))
        {
            _ = PatchAsync(new ItemPatch { Notes = value });
        }
    }

    partial void OnRatingChanged(int value)
    {
        if (!_loading && Item is not null && value != Item.Rating)
        {
            _ = PatchAsync(new ItemPatch { Rating = value });
        }
    }

    partial void OnIsFavoriteChanged(bool value)
    {
        if (!_loading && Item is not null && value != Item.IsFavorite)
        {
            _ = PatchAsync(new ItemPatch { IsFavorite = value });
        }
    }

    partial void OnStatusChanged(LocalizedOption<ItemStatus>? value)
    {
        if (!_loading && Item is not null && value is not null && value.Value != Item.Status)
        {
            _ = PatchAsync(new ItemPatch { Status = value.Value });
        }
    }

    private void Clear()
    {
        Cancel();
        _loading = true;
        try
        {
            Item = null;
            Title = "";
            Notes = "";
            Rating = 0;
            IsFavorite = false;
            Status = null;
            Tags.Reset([]);
        }
        finally
        {
            _loading = false;
        }

        Thumbnail = null;
        CustomDetails = null;
    }

    private FrameworkElement? CreateCustomDetails(LibraryItem item)
    {
        if (_options.CreateDetails is null)
        {
            return null;
        }

        try
        {
            return _options.CreateDetails(item);
        }
        catch (Exception ex)
        {
            _services.Log.Error("CreateDetails failed.", ex);
            return null;
        }
    }

    private async Task LoadThumbnailAsync(LibraryItem item)
    {
        Cancel();
        var load = new CancellationTokenSource();
        _thumbnailLoad = load;
        Thumbnail = null;
        try
        {
            var image = await _services.Thumbnails.GetAsync(item, load.Token);
            if (!load.IsCancellationRequested && Item?.Id == item.Id)
            {
                Thumbnail = image;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _services.Log.Warn("Loading the details thumbnail failed.", ex);
        }
    }

    private async Task SaveTagsAsync()
    {
        if (Item is null)
        {
            return;
        }

        try
        {
            await _services.Library.SetTagsAsync(Item.Id, [.. Tags.Tags]);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private async Task PatchAsync(ItemPatch patch)
    {
        if (Item is null)
        {
            return;
        }

        try
        {
            await _services.Library.UpdateAsync(Item.Id, patch);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void Fail(Exception ex)
    {
        _services.Log.Error("Saving item details failed.", ex);
        _services.Shell.Notify(Loc.Get("Library.Error.SaveFailed"), NotifySeverity.Error);
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = ["Library.Unit.B", "Library.Unit.KB", "Library.Unit.MB", "Library.Unit.GB", "Library.Unit.TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var number = unit == 0 ? value.ToString("N0", CultureInfo.CurrentCulture) : value.ToString("0.#", CultureInfo.CurrentCulture);
        return Loc.Format(units[unit], number);
    }

    private static string FormatDate(DateTimeOffset value) => value.LocalDateTime.ToString("g", CultureInfo.CurrentCulture);
}
