using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Thumbnails;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>
/// One grid tile. The thumbnail is loaded while a container shows the tile (<see cref="Attach"/>) and
/// released when the container is recycled or unloaded (<see cref="Detach"/>); the thumbnail service's
/// memory cache makes reloading cheap.
/// </summary>
internal sealed partial class TileViewModel : ObservableObject
{
    private readonly IThumbnailService _thumbnails;
    private readonly BrowserOptions _options;
    private CancellationTokenSource? _load;
    private bool _attached;

    public TileViewModel(LibraryItem item, IThumbnailService thumbnails, BrowserOptions options)
    {
        _thumbnails = thumbnails;
        _options = options;
        Item = item;
        Compute();
    }

    public LibraryItem Item { get; private set; }

    public long Id => Item.Id;

    public string Title => Item.Title;

    public bool IsFavorite => Item.IsFavorite;

    public bool IsMissing => Item.IsMissing;

    public string? Subtitle { get; private set; }

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    public double ProgressValue { get; private set; }

    public bool HasProgress { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    public partial ImageSource? Thumbnail { get; private set; }

    public bool ShowPlaceholder => Thumbnail is null;

    /// <summary>Replaces the item after a refresh; reloads the thumbnail when its inputs changed.</summary>
    public void Update(LibraryItem item)
    {
        var stampChanged = item.FileSize != Item.FileSize || item.FileModified != Item.FileModified
                           || !string.Equals(item.CoverPath, Item.CoverPath, StringComparison.Ordinal);
        Item = item;
        Compute();
        OnPropertyChanged(string.Empty);
        if (stampChanged && _attached)
        {
            CancelLoad();
            Thumbnail = null;
            StartLoad();
        }
    }

    /// <summary>A container started showing this tile.</summary>
    public void Attach()
    {
        _attached = true;
        if (Thumbnail is null && _load is null)
        {
            StartLoad();
        }
    }

    /// <summary>The container stopped showing this tile.</summary>
    public void Detach()
    {
        _attached = false;
        CancelLoad();
        Thumbnail = null;
    }

    private void StartLoad()
    {
        _load = new CancellationTokenSource();
        _ = LoadAsync(_load);
    }

    private void CancelLoad()
    {
        _load?.Cancel();
        _load = null;
    }

    private async Task LoadAsync(CancellationTokenSource load)
    {
        ImageSource? image;
        try
        {
            image = await _thumbnails.GetAsync(Item, load.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            image = null;
        }
        finally
        {
            if (ReferenceEquals(_load, load))
            {
                _load = null;
            }

            load.Dispose();
        }

        if (_attached && !load.IsCancellationRequested)
        {
            Thumbnail = image;
        }
    }

    private void Compute()
    {
        Subtitle = Safe(_options.Subtitle, Item);
        var fraction = Safe(_options.ProgressFraction, Item);
        HasProgress = fraction is not null;
        ProgressValue = Math.Clamp(fraction ?? 0, 0, 1);
    }

    private static TResult? Safe<TResult>(Func<LibraryItem, TResult?>? func, LibraryItem item)
    {
        if (func is null)
        {
            return default;
        }

        try
        {
            return func(item);
        }
        catch (Exception)
        {
            return default;
        }
    }
}
