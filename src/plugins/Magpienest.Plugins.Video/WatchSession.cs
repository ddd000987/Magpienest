using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.Video;

/// <summary>
/// Turns playback state into library patches: position, duration, watch time and the automatic status
/// (None → InProgress → Completed at 95 % or at the end; OnHold/Dropped/Planned are never changed).
/// </summary>
internal sealed class WatchSession(ItemStatus initialStatus)
{
    /// <summary>A saved position is resumed only between these fractions of the duration.</summary>
    public const double ResumeFrom = 0.05;

    /// <inheritdoc cref="ResumeFrom" />
    public const double ResumeUntil = 0.95;

    /// <summary>Watching past this fraction counts as watched.</summary>
    public const double CompletedAt = 0.95;

    private double _pendingSeconds;
    private bool _reported;

    public ItemStatus Status { get; private set; } = initialStatus;

    /// <summary>Where to start: the saved position when it lies between 5 % and 95 %, otherwise the beginning.</summary>
    public static TimeSpan? StartAt(LibraryItem item)
    {
        if (item.ProgressTotal <= 0 || item.ProgressPosition <= 0)
        {
            return null;
        }

        var fraction = item.ProgressPosition / (double)item.ProgressTotal;
        return fraction is >= ResumeFrom and <= ResumeUntil ? TimeSpan.FromMilliseconds(item.ProgressPosition) : null;
    }

    /// <summary>The patch for one save.</summary>
    /// <param name="position">Current position.</param>
    /// <param name="duration">Duration; zero when not known yet.</param>
    /// <param name="watched">Time spent playing since the last save.</param>
    /// <param name="ended">Playback reached the end: the position goes back to 0.</param>
    public ItemPatch ToPatch(TimeSpan position, TimeSpan duration, TimeSpan watched, bool ended)
    {
        _pendingSeconds += Math.Max(0, watched.TotalSeconds);
        var whole = (long)Math.Floor(_pendingSeconds);
        _pendingSeconds -= whole;

        var finished = ended || (duration > TimeSpan.Zero && position.Ticks >= duration.Ticks * CompletedAt);
        ItemStatus? status = null;
        if (Status is ItemStatus.None or ItemStatus.InProgress or ItemStatus.Completed)
        {
            var next = finished ? ItemStatus.Completed
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
            ProgressPosition = ended ? 0 : (long)Math.Max(0, position.TotalMilliseconds),
            ProgressTotal = duration > TimeSpan.Zero ? (long)duration.TotalMilliseconds : null,
            AddTimeSpentSeconds = whole > 0 ? whole : null,
            Status = status,
            MarkOpened = first,
        };
    }
}
