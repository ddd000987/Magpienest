using System.Globalization;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using Magpienest.Sdk.Archives;

namespace Magpienest.Plugins.Archives;

/// <summary>A file entry as the backend reports it, before key normalisation.</summary>
internal sealed record BackendEntry(string Key, long Size, bool IsEncrypted);

/// <summary>
/// The archive library behind <see cref="ArchiveHandle"/>. Not thread-safe: callers serialise access.
/// Exists so the solid-archive code path can be tested with a fake.
/// </summary>
internal interface IArchiveBackend : IDisposable
{
    bool IsSolid { get; }

    bool IsEncrypted { get; }

    /// <summary>Files only, in archive order.</summary>
    IReadOnlyList<BackendEntry> Entries { get; }

    /// <summary>Random access: copies one entry (by position in <see cref="Entries"/>) into <paramref name="destination"/>.</summary>
    void CopyEntry(int index, Stream destination, CancellationToken ct);

    /// <summary>
    /// One sequential pass over every file entry. <paramref name="onEntry"/> receives the entry's position in
    /// <see cref="Entries"/> and a function that writes its content to a stream.
    /// </summary>
    void ExtractAll(Action<int, Action<Stream>> onEntry, CancellationToken ct);
}

/// <summary>Opens backends; replaced by a fake in tests.</summary>
internal interface IArchiveBackendFactory
{
    IArchiveBackend Open(string path);
}

/// <summary>Entry-name decoding for old archives: strict UTF-8, else a legacy code page.</summary>
internal static class EntryNames
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>Strict UTF-8, falling back to <paramref name="fallback"/> when the bytes are not valid UTF-8.</summary>
    public static string Decode(byte[] bytes, int index, int count, Encoding fallback)
    {
        try
        {
            return StrictUtf8.GetString(bytes, index, count);
        }
        catch (DecoderFallbackException)
        {
            return fallback.GetString(bytes, index, count);
        }
    }

    /// <summary>The system OEM code page (code-page provider must be registered).</summary>
    public static Encoding SystemFallback()
    {
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>'/' separators, no leading separator.</summary>
    public static string NormaliseKey(string key) => key.Replace('\\', '/').TrimStart('/');
}

/// <summary>SharpCompress implementation.</summary>
internal sealed class SharpCompressBackendFactory : IArchiveBackendFactory
{
    private readonly Encoding _fallback;

    public SharpCompressBackendFactory()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _fallback = EntryNames.SystemFallback();
    }

    public IArchiveBackend Open(string path)
    {
        var options = new ReaderOptions
        {
            LeaveStreamOpen = false,
            ArchiveEncoding = new ArchiveEncoding
            {
                CustomDecoder = (bytes, index, count, _) => EntryNames.Decode(bytes, index, count, _fallback),
            },
        };

        SharpCompress.Archives.IArchive archive;
        try
        {
            archive = ArchiveFactory.OpenArchive(path, options);
        }
        catch (CryptographicException ex)
        {
            throw new ArchiveEncryptedException("The archive is password-protected.", ex);
        }
        catch (Exception ex) when (ex is not (IOException or OperationCanceledException or UnauthorizedAccessException))
        {
            throw new IOException($"The archive could not be read: {path}", ex);
        }

        return new SharpCompressBackend(archive);
    }
}

internal sealed class SharpCompressBackend : IArchiveBackend
{
    private readonly SharpCompress.Archives.IArchive _archive;
    private readonly List<IArchiveEntry> _files;

    public SharpCompressBackend(SharpCompress.Archives.IArchive archive)
    {
        _archive = archive;
        try
        {
            _files = [.. archive.Entries.Where(e => !e.IsDirectory)];
            Entries = [.. _files.Select(e => new BackendEntry(e.Key ?? "", e.Size, e.IsEncrypted))];
            IsSolid = archive.IsSolid;
            IsEncrypted = archive.IsEncrypted;
        }
        catch (CryptographicException ex)
        {
            archive.Dispose();
            throw new ArchiveEncryptedException("The archive is password-protected.", ex);
        }
        catch (Exception ex) when (ex is not IOException)
        {
            archive.Dispose();
            throw new IOException("The archive's entry list could not be read.", ex);
        }
    }

    public bool IsSolid { get; }

    public bool IsEncrypted { get; }

    public IReadOnlyList<BackendEntry> Entries { get; }

    public void CopyEntry(int index, Stream destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var source = _files[index].OpenEntryStream();
            source.CopyTo(destination, 81920);
        }
        catch (CryptographicException ex)
        {
            throw new ArchiveEncryptedException("The entry is password-protected.", ex);
        }
        catch (Exception ex) when (ex is not (IOException or OperationCanceledException))
        {
            throw new IOException($"The entry could not be read: {_files[index].Key}", ex);
        }
    }

    public void ExtractAll(Action<int, Action<Stream>> onEntry, CancellationToken ct)
    {
        // Keys may repeat in broken archives: hand out positions in order per key.
        var positions = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var i = 0; i < Entries.Count; i++)
        {
            if (!positions.TryGetValue(Entries[i].Key, out var queue))
            {
                positions[Entries[i].Key] = queue = new Queue<int>();
            }

            queue.Enqueue(i);
        }

        try
        {
            using var reader = _archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                if (entry.IsDirectory || !positions.TryGetValue(entry.Key ?? "", out var queue) || queue.Count == 0)
                {
                    continue;
                }

                onEntry(queue.Dequeue(), reader.WriteEntryTo);
            }
        }
        catch (CryptographicException ex)
        {
            throw new ArchiveEncryptedException("The archive is password-protected.", ex);
        }
        catch (Exception ex) when (ex is not (IOException or OperationCanceledException or ArchiveEncryptedException))
        {
            throw new IOException("The archive could not be extracted.", ex);
        }
    }

    public void Dispose() => _archive.Dispose();
}
