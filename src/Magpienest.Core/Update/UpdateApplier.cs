using System.IO;

namespace Magpienest.Core.Update;

/// <summary>The outcome of <see cref="UpdateApplier.Apply"/>.</summary>
/// <param name="Success">True when every file of the new version is in place.</param>
/// <param name="Error">English reason when <paramref name="Success"/> is false (the old version was put back).</param>
public sealed record UpdateApplyResult(bool Success, string? Error);

/// <summary>
/// Replaces an installed copy of Magpienest with a newer one unpacked elsewhere. The host runs it from the new
/// version's exe, inside the unpacked folder, after the old app exited. Never touches the data folder or
/// <c>portable.txt</c> (they decide where the user's data lives); on failure every file is put back.
/// </summary>
public static class UpdateApplier
{
    /// <summary>The exe both folders must contain.</summary>
    public const string ExeName = "Magpienest.exe";

    private static readonly string[] KeptTopLevel = ["data", "portable.txt"];

    /// <param name="source">The unpacked new version (the folder holding its exe).</param>
    /// <param name="target">The installed version to replace.</param>
    /// <param name="backup">An empty or missing folder for the replaced files (on the same disk is fastest).</param>
    /// <param name="log">Receives English progress lines.</param>
    /// <param name="attempts">Tries per file operation (files can stay locked for a moment after the old app exits).</param>
    /// <param name="retryDelay">Wait between tries.</param>
    public static UpdateApplyResult Apply(string source, string target, string backup, Action<string> log, int attempts = 20, TimeSpan? retryDelay = null)
    {
        var delay = retryDelay ?? TimeSpan.FromMilliseconds(500);
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        backup = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backup));

        if (!File.Exists(Path.Combine(source, ExeName)))
        {
            return new(false, $"'{source}' holds no {ExeName}.");
        }

        if (!File.Exists(Path.Combine(target, ExeName)))
        {
            return new(false, $"'{target}' holds no {ExeName}.");
        }

        // A portable copy unpacks the new version inside its own data folder; anything else overlapping is refused.
        if (IsSameOrInside(target, source) || (IsSameOrInside(source, target) && !IsSameOrInside(source, Path.Combine(target, "data"))))
        {
            return new(false, "The new and the installed version overlap.");
        }

        var done = new List<(string Relative, bool HadOld, bool Copied)>();
        try
        {
            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            Directory.CreateDirectory(backup);
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(source, f))
                .Where(r => !IsKept(r))
                .ToList();
            var incoming = files.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Built-in plugins are mirrored: a file the new version no longer ships would still be loaded.
            var builtIn = Path.Combine(target, "plugins");
            var obsolete = Directory.Exists(builtIn)
                ? Directory.EnumerateFiles(builtIn, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(target, f))
                    .Where(r => !incoming.Contains(r))
                    .ToList()
                : [];
            foreach (var relative in obsolete)
            {
                MoveToBackup(relative);
                done.Add((relative, true, false));
            }

            foreach (var relative in files)
            {
                var destination = Path.Combine(target, relative);
                var hadOld = File.Exists(destination);
                if (hadOld)
                {
                    MoveToBackup(relative);
                }

                done.Add((relative, hadOld, false));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Retry(() => File.Copy(Path.Combine(source, relative), destination, overwrite: true));
                done[^1] = (relative, hadOld, true);
            }

            log($"Updated {files.Count} files in '{target}' ({obsolete.Count} obsolete files moved to '{backup}').");
            return new(true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Updating '{target}' failed: {ex.Message}. Putting the old files back.");
            for (var i = done.Count - 1; i >= 0; i--)
            {
                var (relative, hadOld, copied) = done[i];
                try
                {
                    var destination = Path.Combine(target, relative);
                    if (copied)
                    {
                        Retry(() => File.Delete(destination));
                    }

                    if (hadOld)
                    {
                        Retry(() => File.Move(Path.Combine(backup, relative), destination, overwrite: true));
                    }
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    log($"Putting back '{relative}' failed: {restore.Message}");
                }
            }

            return new(false, ex.Message);
        }

        void MoveToBackup(string relative)
        {
            var saved = Path.Combine(backup, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            Retry(() => File.Move(Path.Combine(target, relative), saved, overwrite: true));
        }

        void Retry(Action action)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception ex) when (attempt < attempts && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(delay);
                }
            }
        }
    }

    private static bool IsKept(string relative)
    {
        var first = relative.Split(Path.DirectorySeparatorChar, 2)[0];
        return KeptTopLevel.Any(k => string.Equals(k, first, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSameOrInside(string path, string folder) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
