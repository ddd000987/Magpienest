using Magpienest.Sdk.Archives;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.Comic;

/// <summary>
/// Books under a folder: archive files, and folders that directly contain images. Never opens archives,
/// so a scan stays fast.
/// </summary>
internal sealed class ComicScanner(IImagingService imaging, IArchiveService? archives) : IItemScanner
{
    private static readonly EnumerationOptions Direct = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    public IEnumerable<ScannedEntry> Scan(string root, CancellationToken ct)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            var results = new List<ScannedEntry>();
            long imageBytes = 0;
            var imageCount = 0;
            var newest = DateTime.MinValue;
            var subdirectories = new List<DirectoryInfo>();
            try
            {
                foreach (var file in dir.EnumerateFiles("*", Direct))
                {
                    if (archives is not null && archives.IsSupported(file.Name))
                    {
                        results.Add(new ScannedEntry(file.FullName, Path.GetFileNameWithoutExtension(file.Name), file.Length,
                                                     new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
                    }
                    else if (!file.Name.StartsWith('.') && imaging.IsSupported(file.Extension))
                    {
                        imageCount++;
                        imageBytes += file.Length;
                        if (file.LastWriteTimeUtc > newest)
                        {
                            newest = file.LastWriteTimeUtc;
                        }
                    }
                }

                subdirectories.AddRange(dir.EnumerateDirectories("*", Direct)
                                           .Where(d => !d.Name.StartsWith('.') && !d.Name.StartsWith('$')));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // unreadable folder: skipped silently
            }

            if (imageCount > 0)
            {
                yield return new ScannedEntry(dir.FullName, BookTitle(dir), imageBytes,
                                              new DateTimeOffset(newest, TimeSpan.Zero));
            }

            foreach (var entry in results)
            {
                yield return entry;
            }

            // Reverse so siblings are visited in natural order.
            foreach (var sub in subdirectories.OrderByDescending(d => d.Name, BookPages.Natural))
            {
                pending.Push(sub);
            }
        }
    }

    private static string BookTitle(DirectoryInfo dir) => string.IsNullOrEmpty(dir.Name) ? dir.FullName : dir.Name;
}
