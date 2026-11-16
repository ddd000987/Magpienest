using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>A package that was validated and extracted; see <see cref="PluginInstaller.Prepare"/>.</summary>
/// <param name="Manifest">The package's manifest.</param>
/// <param name="Folder">&lt;data&gt;/plugins/&lt;id&gt; for a new plugin, &lt;data&gt;/plugins/.staging/&lt;id&gt; for a staged update.</param>
/// <param name="Staged">True when <paramref name="Folder"/> is the staging folder.</param>
internal sealed record PreparedInstall(PluginManifest Manifest, string Folder, bool Staged);

/// <summary>
/// File-system side of installing and removing plugins (phase-5 T5.1). Everything stays inside
/// &lt;data&gt;/plugins. None of these methods touch kernel state, so they may run on any thread.
/// </summary>
internal static class PluginInstaller
{
    private const string StagingName = ".staging";

    public static string StagingRoot(string pluginsRoot) => Path.Combine(pluginsRoot, StagingName);

    /// <summary>
    /// Validates the package and extracts it: straight into &lt;plugins&gt;/&lt;id&gt; when the id is new, otherwise
    /// into the staging folder. Throws <see cref="PackageException"/> for a rejected package and
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the disk fails.
    /// </summary>
    /// <param name="packagePath">Zip file or folder.</param>
    /// <param name="pluginsRoot">&lt;data&gt;/plugins.</param>
    /// <param name="builtInIds">Built-in plugin ids (rejected).</param>
    /// <param name="loadedIds">Ids of every plugin the kernel already lists.</param>
    /// <param name="alwaysStage">Safe mode: nothing new is activated, so everything is staged.</param>
    public static PreparedInstall Prepare(string packagePath, string pluginsRoot, ISet<string> builtInIds, ISet<string> loadedIds, bool alwaysStage)
    {
        using var package = PluginPackage.Open(packagePath, pluginsRoot, builtInIds);
        var id = package.Manifest.Id;
        var staging = StagingRoot(pluginsRoot);
        var staged = alwaysStage || loadedIds.Contains(id) || Directory.Exists(Path.Combine(pluginsRoot, id));
        var partial = Path.Combine(staging, id + "~partial");
        var target = staged ? Path.Combine(staging, id) : Path.Combine(pluginsRoot, id);
        try
        {
            Directory.CreateDirectory(staging);
            DeleteDirectory(partial);
            package.Extract(partial);
            if (staged)
            {
                DeleteDirectory(target);
            }

            MoveDirectory(partial, target, attempts: 10);
        }
        catch (PackageException ex)
        {
            TryDelete(partial);
            throw new PackageException(ex.Code, ex.Message, id);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        return new PreparedInstall(package.Manifest, target, staged);
    }

    /// <summary>Validates without extracting.</summary>
    public static PluginManifest Inspect(string packagePath, string pluginsRoot, ISet<string> builtInIds)
    {
        using var package = PluginPackage.Open(packagePath, pluginsRoot, builtInIds);
        return package.Manifest;
    }

    /// <summary>
    /// Start-up step before discovery: deletes the folders of <c>pendingRemoval</c> ids and replaces the folders
    /// of <c>pendingInstall</c> ids with their staged copies. Failures are logged and the id stays queued for the
    /// next start. Never throws.
    /// </summary>
    public static void ProcessQueues(KernelStateStore state, string pluginsRoot, ILog log)
    {
        try
        {
            var removals = state.PendingRemoval.ToList();
            var installs = state.PendingInstall.ToList();
            var staging = StagingRoot(pluginsRoot);
            if (removals.Count == 0 && installs.Count == 0 && !Directory.Exists(staging))
            {
                return;
            }

            var keptRemovals = new List<string>();
            foreach (var id in removals)
            {
                if (!ManifestReader.IsValidId(id))
                {
                    log.Warn($"Ignoring invalid id '{id}' in pendingRemoval.");
                    continue;
                }

                try
                {
                    RemoveFolder(pluginsRoot, id);
                    log.Info($"Removed plugin '{id}'.");
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    log.Error($"Could not remove plugin '{id}'; will retry at the next start.", ex);
                    keptRemovals.Add(id);
                }
            }

            var keptInstalls = new List<string>();
            foreach (var id in installs)
            {
                if (!ManifestReader.IsValidId(id))
                {
                    log.Warn($"Ignoring invalid id '{id}' in pendingInstall.");
                    continue;
                }

                var stagedFolder = Path.Combine(staging, id);
                if (!Directory.Exists(stagedFolder))
                {
                    log.Warn($"The staged update of plugin '{id}' is missing; dropped.");
                    continue;
                }

                try
                {
                    ReplaceFolder(pluginsRoot, id, stagedFolder);
                    log.Info($"Updated plugin '{id}'.");
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    log.Error($"Could not update plugin '{id}'; will retry at the next start.", ex);
                    keptInstalls.Add(id);
                }
            }

            if (!keptRemovals.SequenceEqual(removals) || !keptInstalls.SequenceEqual(installs))
            {
                state.SetQueues(keptInstalls, keptRemovals);
            }

            SweepStaging(staging, keptInstalls);
        }
        catch (Exception ex)
        {
            log.Error("Processing the plugin install queue failed.", ex);
        }
    }

    private static void RemoveFolder(string pluginsRoot, string id)
    {
        var folder = ResolveInstalledFolder(pluginsRoot, id);
        if (folder is null)
        {
            return;
        }

        // A rename fails as a whole when any file in the folder is in use, so a failure leaves the plugin intact.
        var trash = MoveAside(pluginsRoot, folder, id);
        TryDelete(trash);
    }

    private static void ReplaceFolder(string pluginsRoot, string id, string stagedFolder)
    {
        var target = ResolveInstalledFolder(pluginsRoot, id) ?? Path.Combine(pluginsRoot, id);
        string? trash = null;
        if (Directory.Exists(target))
        {
            trash = MoveAside(pluginsRoot, target, id);
        }

        try
        {
            MoveDirectory(stagedFolder, target, attempts: 3);
        }
        catch
        {
            if (trash is not null && !Directory.Exists(target))
            {
                try
                {
                    MoveDirectory(trash, target, attempts: 3);
                }
                catch (Exception ex) when (IsFileError(ex))
                {
                    // The old copy stays in the staging folder; it is not lost.
                }
            }

            throw;
        }

        if (trash is not null)
        {
            TryDelete(trash);
        }
    }

    private static string MoveAside(string pluginsRoot, string folder, string id)
    {
        var staging = StagingRoot(pluginsRoot);
        Directory.CreateDirectory(staging);
        var trash = Path.Combine(staging, $"{id}~old-{Guid.NewGuid():N}");
        MoveDirectory(folder, trash, attempts: 3);
        return trash;
    }

    /// <summary>&lt;plugins&gt;/&lt;id&gt; when it exists, else the folder whose plugin.json declares the id.</summary>
    private static string? ResolveInstalledFolder(string pluginsRoot, string id)
    {
        var direct = Path.Combine(pluginsRoot, id);
        if (Directory.Exists(direct))
        {
            return direct;
        }

        if (!Directory.Exists(pluginsRoot))
        {
            return null;
        }

        foreach (var directory in Directory.GetDirectories(pluginsRoot))
        {
            if (Path.GetFileName(directory).StartsWith('.'))
            {
                continue;
            }

            try
            {
                var manifestPath = Path.Combine(directory, "plugin.json");
                if (File.Exists(manifestPath) && ManifestReader.Read(manifestPath).Id == id)
                {
                    return directory;
                }
            }
            catch (Exception ex) when (IsFileError(ex) || ex is InvalidDataException)
            {
                // not a readable plugin folder
            }
        }

        return null;
    }

    /// <summary>Deletes staging leftovers (partial extractions, moved-aside folders, dropped updates).</summary>
    private static void SweepStaging(string staging, IReadOnlyCollection<string> keep)
    {
        if (!Directory.Exists(staging))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(staging))
        {
            if (!keep.Contains(Path.GetFileName(directory), StringComparer.Ordinal))
            {
                TryDelete(directory);
            }
        }
    }

    /// <summary>
    /// Renames a folder. A virus scanner may hold freshly written files for a moment, which makes the rename
    /// fail with a sharing violation, so it is retried a few times.
    /// </summary>
    private static void MoveDirectory(string source, string destination, int attempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when (IsFileError(ex) && attempt < attempts)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            DeleteDirectory(path);
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            // best effort; the next start sweeps the staging folder again
        }
    }

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException;
}
