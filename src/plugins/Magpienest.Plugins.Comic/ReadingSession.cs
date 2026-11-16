using Magpienest.Sdk.Library;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.Comic;

/// <summary>Per-book JSON in <see cref="LibraryItem.Extra"/>; null = use the viewer default.</summary>
internal sealed class ComicExtra
{
    /// <summary>"rtl" | "ltr" | null.</summary>
    public string? Direction { get; set; }

    /// <summary>"single" | "double" | null.</summary>
    public string? Spread { get; set; }

    public bool? CoverAlone { get; set; }

    /// <summary>"paged" | "strip" | null.</summary>
    public string? Flow { get; set; }

    public bool HasOverrides => Direction is not null || Spread is not null || CoverAlone is not null || Flow is not null;

    public static PageFlow? ParseFlow(string? value) => value switch
    {
        "paged" => PageFlow.Paged,
        "strip" => PageFlow.Strip,
        _ => null,
    };

    public static string Format(PageFlow value) => value == PageFlow.Strip ? "strip" : "paged";

    public static ReadingDirection? ParseDirection(string? value) => value switch
    {
        "rtl" => ReadingDirection.RightToLeft,
        "ltr" => ReadingDirection.LeftToRight,
        _ => null,
    };

    public static SpreadMode? ParseSpread(string? value) => value switch
    {
        "single" => SpreadMode.Single,
        "double" => SpreadMode.Double,
        _ => null,
    };

    public static string Format(ReadingDirection value) => value == ReadingDirection.RightToLeft ? "rtl" : "ltr";

    public static string Format(SpreadMode value) => value == SpreadMode.Single ? "single" : "double";

    /// <summary>
    /// The request's book settings: null when the book has no override (the viewer uses its defaults);
    /// otherwise the overrides, with unset fields taken from the built-in defaults.
    /// </summary>
    public BookSettings? ToBookSettings() => HasOverrides
        ? new BookSettings(ParseDirection(Direction) ?? ReadingDirection.RightToLeft,
                           ParseSpread(Spread) ?? SpreadMode.Double,
                           CoverAlone ?? true,
                           ParseFlow(Flow) ?? PageFlow.Paged)
        : null;

    public static ComicExtra From(BookSettings settings) => new()
    {
        Direction = Format(settings.Direction),
        Spread = Format(settings.Spread),
        CoverAlone = settings.CoverAlone,
        Flow = Format(settings.Flow),
    };
}

/// <summary>
/// Turns viewer progress reports into library patches: position, page count, reading time and the
/// automatic status (None → InProgress → Completed; OnHold/Dropped/Planned are never changed).
/// </summary>
internal sealed class ReadingSession(ItemStatus initialStatus)
{
    private double _pendingSeconds;
    private bool _reported;

    public ItemStatus Status { get; private set; } = initialStatus;

    /// <summary>Where to open a book: the saved position, or the start when it was finished.</summary>
    public static int StartPage(LibraryItem item, int pageCount)
    {
        if (pageCount <= 0 || item.Status == ItemStatus.Completed)
        {
            return 0;
        }

        return (int)Math.Clamp(item.ProgressPosition, 0, pageCount - 1);
    }

    public ItemPatch ToPatch(ViewerProgress progress)
    {
        _pendingSeconds += Math.Max(0, progress.ElapsedSinceLastReport.TotalSeconds);
        var whole = (long)Math.Floor(_pendingSeconds);
        _pendingSeconds -= whole;

        ItemStatus? status = null;
        if (Status is ItemStatus.None or ItemStatus.InProgress or ItemStatus.Completed)
        {
            var next = progress.ReachedEnd ? ItemStatus.Completed
                     : Status == ItemStatus.None ? ItemStatus.InProgress
                     : Status;
            if (next != Status)
            {
                status = next;
                Status = next;
            }
        }

        var first = !_reported;
        _reported = true;
        return new ItemPatch
        {
            ProgressPosition = progress.PageIndex,
            ProgressTotal = progress.PageCount,
            AddTimeSpentSeconds = whole > 0 ? whole : null,
            Status = status,
            MarkOpened = first,
        };
    }
}
