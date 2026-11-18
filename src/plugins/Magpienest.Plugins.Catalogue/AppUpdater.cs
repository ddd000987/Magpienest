using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Magpienest.Sdk;

namespace Magpienest.Plugins.Catalogue;

/// <summary>What <see cref="AppUpdater.PrepareAsync"/> ended with.</summary>
internal enum AppUpdateOutcome
{
    /// <summary>The new version is unpacked and checked; <see cref="AppUpdater.Launch"/> may start it.</summary>
    Ready,

    /// <summary>Magpienest's folder cannot be written (for example under Program Files): update by hand.</summary>
    NotWritable,

    /// <summary>The download or the unpacked files were not as expected.</summary>
    Failed,
}

/// <summary>How the last update ended, as the host wrote it (read once at the next start).</summary>
/// <param name="Version">The version that was applied.</param>
/// <param name="Success">False when the old version was put back.</param>
/// <param name="Error">English reason, for the log.</param>
internal sealed record AppUpdateReport(string Version, bool Success, string? Error);

/// <summary>
/// Updates Magpienest itself: downloads the release zip the catalogue lists (size and SHA-256 checked), unpacks it
/// into the cache, and starts the new exe with --apply-update; that exe waits for this app to exit, copies itself
/// over the installed folder and starts it again (see the host's ApplyUpdate). The data folder is never touched.
/// </summary>
internal sealed class AppUpdater
{
    private const string ResultFileName = "result.json";

    private readonly IAppEnvironment _environment;
    private readonly ILog _log;

    public AppUpdater(IAppEnvironment environment, ILog log)
    {
        _environment = environment;
        _log = log;
    }

    /// <summary>The cache folder for downloads, unpacked versions and the host's result file.</summary>
    public string UpdatesDirectory => Path.Combine(_environment.CacheDirectory, "updates");

    /// <summary>The running version as "major.minor.patch".</summary>
    public string CurrentVersion => _environment.AppVersion.ToString(3);

    /// <summary>The catalogue's release when it is newer than the running app; otherwise null.</summary>
    public CatalogueApp? NewerThanRunning(StoreCatalogue? catalogue) =>
        catalogue?.App is { } app && SemVer.Compare(app.Version, CurrentVersion) > 0 ? app : null;

    /// <summary>Downloads and unpacks <paramref name="app"/>; the result says whether it can be launched.</summary>
    /// <exception cref="StoreException">The download failed or was not as the catalogue says.</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public async Task<(AppUpdateOutcome Outcome, string? StagedExe)> PrepareAsync(CatalogueApp app, IPackageDownloader downloader, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (!CanWrite(_environment.AppDirectory))
        {
            _log.Warn($"'{_environment.AppDirectory}' cannot be written; Magpienest must be updated by hand.");
            return (AppUpdateOutcome.NotWritable, null);
        }

        var root = UpdatesDirectory;
        TryDeleteDirectory(root);
        Directory.CreateDirectory(root);
        var zip = await downloader.DownloadAsync(app.Package, app.Version, Path.Combine(root, "download"), progress, cancellationToken);
        var staged = Path.Combine(root, app.Version);
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, staged, overwriteFiles: true), cancellationToken);
        TryDelete(zip);

        // The release zip holds one folder (Magpienest\) with the exe; accept the exe at the root too.
        var exe = new[] { staged }.Concat(Directory.EnumerateDirectories(staged))
            .Select(d => Path.Combine(d, UpdateFiles.ExeName))
            .FirstOrDefault(File.Exists);
        if (exe is null)
        {
            _log.Warn($"The Magpienest {app.Version} zip holds no {UpdateFiles.ExeName}.");
            return (AppUpdateOutcome.Failed, null);
        }

        var fileVersion = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? string.Empty;
        if (SemVer.Compare(fileVersion.Split('+')[0], app.Version) != 0)
        {
            _log.Warn($"The unpacked exe is version '{fileVersion}', the catalogue says {app.Version}.");
            return (AppUpdateOutcome.Failed, null);
        }

        return (AppUpdateOutcome.Ready, exe);
    }

    /// <summary>The arguments that make <paramref name="stagedExe"/> apply itself and start Magpienest again.</summary>
    public IReadOnlyList<string> LaunchArguments(int processId) =>
    [
        "--apply-update", _environment.AppDirectory,
        "--wait-for-pid", processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--data-dir", _environment.DataDirectory,
        "--update-result", Path.Combine(UpdatesDirectory, ResultFileName),
        .. _environment.CommandLineArgs,
    ];

    /// <summary>Starts the unpacked exe and shuts this app down; the update happens after it exited.</summary>
    public void Launch(string stagedExe)
    {
        var info = new ProcessStartInfo(stagedExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(stagedExe)! };
        foreach (var argument in LaunchArguments(Environment.ProcessId))
        {
            info.ArgumentList.Add(argument);
        }

        using var started = Process.Start(info) ?? throw new InvalidOperationException("The new version did not start.");
        _log.Info($"Started '{stagedExe}' to update Magpienest; shutting down.");
        _environment.RequestShutdown();
    }

    /// <summary>Reads (once) how the last update ended, then removes what it left in the cache.</summary>
    public AppUpdateReport? TakeReport()
    {
        var root = UpdatesDirectory;
        if (!Directory.Exists(root))
        {
            return null;
        }

        AppUpdateReport? report = null;
        var file = Path.Combine(root, ResultFileName);
        try
        {
            if (File.Exists(file))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var element = document.RootElement;
                report = new AppUpdateReport(
                    element.TryGetProperty("version", out var v) ? v.GetString() ?? "?" : "?",
                    element.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True,
                    element.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _log.Warn($"Reading the update result failed: {ex.Message}");
        }

        // The unpacked version may still be exiting; whatever stays is removed at a later start.
        TryDeleteDirectory(root);
        return report;
    }

    private static bool CanWrite(string directory)
    {
        var probe = Path.Combine(directory, $".magpienest-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Info($"'{directory}' could not be removed yet: {ex.Message}");
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed with the folder later.
        }
    }
}

/// <summary>Names shared with the host's updater (a plugin cannot reference Magpienest.Core).</summary>
internal static class UpdateFiles
{
    public const string ExeName = "Magpienest.exe";
}
