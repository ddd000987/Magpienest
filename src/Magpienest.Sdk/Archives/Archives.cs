using System.IO;

namespace Magpienest.Sdk.Archives;

/// <summary>Provided by the Archives plugin: read-only access to zip/rar/7z files.</summary>
public interface IArchiveService
{
    /// <summary>Lower-case with dot: .zip .cbz .rar .cbr .7z .cb7.</summary>
    IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>Whether the path has a supported archive extension.</summary>
    /// <param name="path">A path or an extension.</param>
    bool IsSupported(string path);

    /// <summary>
    /// Opens an archive. Throws <see cref="ArchiveEncryptedException"/> for password-protected archives,
    /// <see cref="IOException"/> for corrupt ones.
    /// </summary>
    /// <param name="path">Archive file path.</param>
    /// <param name="ct">Cancellation.</param>
    Task<IArchive> OpenAsync(string path, CancellationToken ct = default);
}

/// <summary>An open archive.</summary>
public interface IArchive : IAsyncDisposable
{
    /// <summary>Archive file path.</summary>
    string Path { get; }

    /// <summary>Files only, in original order.</summary>
    IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>Returns a seekable stream positioned at 0; the caller disposes it. Thread-safe.</summary>
    /// <param name="entry">An entry of <see cref="Entries"/>.</param>
    /// <param name="ct">Cancellation.</param>
    Task<Stream> OpenEntryAsync(ArchiveEntry entry, CancellationToken ct = default);
}

/// <summary>A file inside an archive.</summary>
/// <param name="Index">Position in <see cref="IArchive.Entries"/>.</param>
/// <param name="Key">Path inside the archive with '/' separators.</param>
/// <param name="Size">Uncompressed size in bytes.</param>
public sealed record ArchiveEntry(int Index, string Key, long Size);

/// <summary>The archive is password-protected.</summary>
public sealed class ArchiveEncryptedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ArchiveEncryptedException() { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Message.</param>
    public ArchiveEncryptedException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Message.</param>
    /// <param name="inner">Cause.</param>
    public ArchiveEncryptedException(string message, Exception inner) : base(message, inner) { }
}
