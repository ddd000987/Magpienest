using System.IO;

namespace Magpienest.Sdk.Viewing;

/// <summary>A borderless window that shows one image. Provided by the Image Window plugin.</summary>
public interface IImageWindowService
{
    /// <summary>
    /// Reads the image into memory, decodes it, and shows a new window. Completes once the window is shown.
    /// Throws <see cref="Imaging.ImageDecodeException"/> or <see cref="IOException"/>; callers notify the error.
    /// </summary>
    /// <param name="request">The image to show.</param>
    /// <param name="ct">Cancellation.</param>
    Task OpenAsync(ImageWindowRequest request, CancellationToken ct = default);
}

/// <summary>An image for <see cref="IImageWindowService"/>.</summary>
public sealed record ImageWindowRequest
{
    /// <summary>Window title (taskbar, Alt+Tab).</summary>
    public required string Name { get; init; }

    /// <summary>Opens the image stream. Called once; the bytes are copied into memory before it is disposed.</summary>
    public required Func<CancellationToken, Task<Stream>> Open { get; init; }

    /// <summary>The file on disk, when there is one; enables "Show in Explorer".</summary>
    public string? FilePath { get; init; }
}
