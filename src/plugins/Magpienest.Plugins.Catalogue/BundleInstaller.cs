using System.IO;
using Magpienest.Sdk;

namespace Magpienest.Plugins.Catalogue;

/// <summary>Result of <see cref="BundleInstaller.InstallAsync"/>.</summary>
/// <param name="Success">Every package was installed (or staged).</param>
/// <param name="RestartRequired">At least one package was staged as an update and needs a restart.</param>
/// <param name="Error">Why it failed; null on success.</param>
/// <param name="InstallError">The kernel's reason when <paramref name="Error"/> is <see cref="StoreError.InstallFailed"/>.</param>
/// <param name="PackageId">The package that failed, when known.</param>
/// <param name="Message">English description for the log; null on success.</param>
internal sealed record BundleInstallOutcome(
    bool Success,
    bool RestartRequired,
    StoreError? Error = null,
    PluginInstallError InstallError = PluginInstallError.None,
    string? PackageId = null,
    string? Message = null);

/// <summary>
/// Installs one bundle: downloads and verifies every package first, then installs them in
/// catalogue order with <see cref="IPluginManager.InstallAsync"/>. When one is rejected, the packages this run
/// newly installed are uninstalled again. Every downloaded zip is deleted afterwards, whatever the result.
/// Call on the UI thread; downloads run in the background.
/// </summary>
internal sealed class BundleInstaller(IPluginManager manager, IPackageDownloader downloader, string downloadDirectory, ILog log)
{
    /// <summary>
    /// Installs <paramref name="bundle"/>. Cancellation is honoured while downloading (nothing is installed then)
    /// and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="bundle">The bundle.</param>
    /// <param name="progress">Download progress 0..1, reported from a background thread.</param>
    /// <param name="cancellationToken">Cancels the downloads.</param>
    public async Task<BundleInstallOutcome> InstallAsync(CatalogueBundle bundle, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        try
        {
            var total = Math.Max(1, bundle.TotalSize);
            long done = 0;
            foreach (var package in bundle.Packages)
            {
                var offset = done;
                var bytes = progress is null ? null : new ProgressReporter<long>(b => progress.Report(Math.Clamp((offset + b) / (double)total, 0, 1)));
                try
                {
                    files.Add(await downloader.DownloadAsync(package, bundle.Version, downloadDirectory, bytes, cancellationToken));
                }
                catch (StoreException ex)
                {
                    log.Warn($"Downloading '{package.Id}' of bundle '{bundle.Id}' failed: {ex.Message}");
                    return new BundleInstallOutcome(false, false, ex.Code, PackageId: package.Id, Message: ex.Message);
                }

                done += package.Size;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await InstallDownloadedAsync(bundle, files);
        }
        finally
        {
            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Warn($"Could not delete the download '{file}'.", ex);
                }
            }
        }
    }

    private async Task<BundleInstallOutcome> InstallDownloadedAsync(CatalogueBundle bundle, IReadOnlyList<string> files)
    {
        // Check every package before the first one is installed, so a package that is not what the catalogue
        // says never gets to run, not even until the rollback.
        for (var i = 0; i < bundle.Packages.Count; i++)
        {
            var package = bundle.Packages[i];
            var info = await manager.InspectAsync(files[i]);
            var code = PluginInstallError.ManifestInvalid;
            string? error = null;
            if (!info.IsValid)
            {
                (code, error) = (info.ErrorCode, info.Error ?? "The package is not valid.");
            }
            else if (info.Manifest?.Id != package.Id)
            {
                error = $"The package holds '{info.Manifest?.Id}', not '{package.Id}'.";
            }
            else if (info.Manifest.Version != bundle.Version)
            {
                error = $"The package holds version {info.Manifest.Version}, not {bundle.Version}.";
            }

            if (error is not null)
            {
                log.Warn($"Package '{package.Id}' of bundle '{bundle.Id}' was refused before installing: {error}");
                return new BundleInstallOutcome(false, false, StoreError.InstallFailed, code, package.Id, error);
            }
        }

        // Only plugins that did not exist before can be undone; an update of an existing one is staged.
        var before = manager.Plugins.Select(p => p.Manifest.Id).ToHashSet(StringComparer.Ordinal);
        var installedNow = new List<string>();
        var restart = false;
        for (var i = 0; i < bundle.Packages.Count; i++)
        {
            var package = bundle.Packages[i];
            var result = await manager.InstallAsync(files[i]);
            if (result.Success && result.PluginId is { } id && !before.Contains(id) && !installedNow.Contains(id))
            {
                installedNow.Add(id);
            }

            if (result.Success && result.PluginId != package.Id)
            {
                result = result with
                {
                    Success = false,
                    Error = $"The package holds '{result.PluginId}', not '{package.Id}'.",
                    ErrorCode = PluginInstallError.ManifestInvalid,
                };
            }

            if (!result.Success)
            {
                log.Warn($"Installing '{package.Id}' of bundle '{bundle.Id}' failed: {result.Error}");
                RollBack(installedNow);
                return new BundleInstallOutcome(false, false, StoreError.InstallFailed, result.ErrorCode, package.Id, result.Error);
            }

            restart |= result.RestartRequired;
        }

        log.Info($"Bundle '{bundle.Id}' {bundle.Version} installed{(restart ? " (restart required)" : string.Empty)}.");
        return new BundleInstallOutcome(true, restart);
    }

    private void RollBack(List<string> installedNow)
    {
        for (var i = installedNow.Count - 1; i >= 0; i--)
        {
            try
            {
                manager.Uninstall(installedNow[i]);
                log.Info($"Rolled back '{installedNow[i]}'.");
            }
            catch (InvalidOperationException ex)
            {
                log.Warn($"Could not roll back '{installedNow[i]}'.", ex);
            }
        }
    }
}

/// <summary>Reports synchronously on the reporting thread (unlike <see cref="Progress{T}"/>).</summary>
internal sealed class ProgressReporter<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
