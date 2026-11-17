using System.Windows;

namespace Magpienest.Sdk.Viewing;

/// <summary>
/// Hover preview: when the mouse rests on an element for a moment, its picture is shown large in a borderless
/// popup beside the cursor (never under it); the wheel turns the pages of a book; the popup goes away when the
/// mouse leaves the element. Provided by the Hover Preview plugin.
/// </summary>
public interface IHoverPreviewService
{
    /// <summary>
    /// Call when the mouse enters <paramref name="target"/>. From there the service follows the mouse itself:
    /// it waits for the delay, shows the preview, turns pages on the wheel and closes when the mouse leaves the
    /// element, the element is clicked or its data context changes. A new call replaces the previous one.
    /// UI thread only.
    /// </summary>
    /// <param name="target">The element under the mouse.</param>
    /// <param name="request">What to show.</param>
    void Hover(FrameworkElement target, HoverPreviewRequest request);
}

/// <summary>What a hover preview shows.</summary>
public sealed record HoverPreviewRequest
{
    /// <summary>Name of what is shown (for the log).</summary>
    public required string Title { get; init; }

    /// <summary>Opens the pages; called once per preview, on a background thread. The service disposes the source.</summary>
    public required Func<CancellationToken, Task<IPageSource>> OpenPages { get; init; }

    /// <summary>Page shown first.</summary>
    public int StartIndex { get; init; }
}
