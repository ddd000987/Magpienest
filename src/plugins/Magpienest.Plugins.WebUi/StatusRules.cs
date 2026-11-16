using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.WebUi;

/// <summary>
/// The automatic status changes of 03-data-model §4, for progress reported by the web page: automatic changes only
/// move <c>None → InProgress → Completed</c> and never touch a status the user set to <c>OnHold</c>, <c>Dropped</c>
/// or <c>Planned</c>.
/// </summary>
internal static class StatusRules
{
    /// <summary>Kinds whose status follows reading or watching progress.</summary>
    private static readonly HashSet<string> TrackedKinds = new(StringComparer.Ordinal) { "comic", "video" };

    /// <summary>The share of a video that counts as watched to the end.</summary>
    public const double VideoFinishedShare = 0.95;

    /// <summary>
    /// The new status after progress was reported, or null when it stays as it is.
    /// </summary>
    /// <param name="kind">Item kind.</param>
    /// <param name="current">The stored status.</param>
    /// <param name="reachedEnd">The last page was shown, or the video was watched to the end.</param>
    public static ItemStatus? AfterProgress(string kind, ItemStatus current, bool reachedEnd)
    {
        if (!TrackedKinds.Contains(kind) || current is not (ItemStatus.None or ItemStatus.InProgress or ItemStatus.Completed))
        {
            return null;
        }

        var next = reachedEnd ? ItemStatus.Completed
                 : current == ItemStatus.None ? ItemStatus.InProgress
                 : current;
        return next == current ? null : next;
    }

    /// <summary>Whether a progress report reached the end: the client says so, or a video passed 95 %.</summary>
    /// <param name="kind">Item kind.</param>
    /// <param name="reported">What the client reported.</param>
    /// <param name="position">Reported position (video: milliseconds).</param>
    /// <param name="total">Reported or stored total.</param>
    public static bool ReachedEnd(string kind, bool reported, long? position, long total) =>
        reported || (kind == "video" && position is { } p && total > 0 && p >= total * VideoFinishedShare);
}
