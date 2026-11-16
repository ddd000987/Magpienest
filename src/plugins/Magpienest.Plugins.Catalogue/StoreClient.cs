using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Magpienest.Plugins.Catalogue;

/// <summary>Downloads one package of a bundle to a local zip.</summary>
internal interface IPackageDownloader
{
    /// <summary>
    /// Downloads and verifies <paramref name="package"/>; returns the path of the verified zip.
    /// Throws <see cref="StoreException"/> on failure (nothing is left behind) and
    /// <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    Task<string> DownloadAsync(CataloguePackage package, string version, string directory, IProgress<long>? progress, CancellationToken cancellationToken);
}

/// <summary>HTTP access to the store (phase 8 T8.1, "Download rules"). Sends nothing but the request and a User-Agent.</summary>
internal sealed class StoreClient : IPackageDownloader, IDisposable
{
    /// <summary>No package may be larger than this.</summary>
    public const long MaxPackageBytes = 500L * 1024 * 1024;

    private const int MaxCatalogueBytes = 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan CatalogueTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly StorePolicy _policy;

    public StoreClient(StorePolicy policy, Version appVersion)
    {
        _policy = policy;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Magpienest", appVersion.ToString(3)));
    }

    public StorePolicy Policy => _policy;

    /// <summary>Fetches and parses the catalogue. Throws <see cref="StoreException"/> on failure.</summary>
    public async Task<StoreCatalogue> FetchCatalogueAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CatalogueTimeout);
        try
        {
            using var response = await GetAsync(_policy.CatalogueUrl, package: false, timeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaxCatalogueBytes)
            {
                throw new StoreException(StoreError.BadCatalogue, "The catalogue is larger than 1 MB.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxCatalogueBytes)
                {
                    throw new StoreException(StoreError.BadCatalogue, "The catalogue is larger than 1 MB.");
                }

                buffer.Write(chunk, 0, read);
            }

            return CatalogueParser.Parse(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StoreException(StoreError.Offline, "The catalogue request timed out.", ex);
        }
        catch (IOException ex)
        {
            throw new StoreException(StoreError.Offline, $"Reading the catalogue failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public Task<string> DownloadAsync(CataloguePackage package, string version, string directory, IProgress<long>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => DownloadCoreAsync(package, version, directory, progress, cancellationToken), cancellationToken);

    public void Dispose() => _http.Dispose();

    private async Task<string> DownloadCoreAsync(CataloguePackage package, string version, string directory, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (!_policy.IsAllowedPackageUrl(package.Url))
        {
            throw new StoreException(StoreError.UrlNotAllowed, $"'{package.Url}' is not in the store repository's releases.");
        }

        if (package.Size > MaxPackageBytes)
        {
            throw new StoreException(StoreError.TooLarge, $"'{package.Id}' is larger than 500 MB.");
        }

        if (!CatalogueParser.IsValidId(package.Id) || !SemVer.IsValid(version) || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new StoreException(StoreError.BadCatalogue, $"'{package.Id}' {version} cannot be used as a file name.");
        }

        var limit = Math.Min(package.Size + (package.Size / 100), MaxPackageBytes);
        var target = Path.Combine(directory, $"{package.Id}-{version}.zip");
        var part = target + ".part";

        try
        {
            Directory.CreateDirectory(directory);
            using var response = await GetAsync(package.Url, package: true, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > limit)
            {
                throw new StoreException(StoreError.TooLarge, $"'{package.Id}' is {response.Content.Headers.ContentLength} bytes, expected {package.Size}.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    int read;
                    using (var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        idle.CancelAfter(ReadTimeout);
                        try
                        {
                            read = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new StoreException(StoreError.Offline, $"The download of '{package.Id}' stopped answering.", ex);
                        }
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > limit)
                    {
                        throw new StoreException(StoreError.TooLarge, $"'{package.Id}' is larger than the {package.Size} bytes the catalogue lists.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(total);
                }
            }

            if (total != package.Size)
            {
                throw new StoreException(StoreError.SizeMismatch, $"'{package.Id}' is {total} bytes, expected {package.Size}.");
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, package.Sha256, StringComparison.Ordinal))
            {
                throw new StoreException(StoreError.HashMismatch, $"'{package.Id}' has SHA-256 {actual}, expected {package.Sha256}.");
            }

            File.Move(part, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(part);
            throw ex is HttpIOException or IOException { InnerException: System.Net.Sockets.SocketException }
                ? new StoreException(StoreError.Offline, $"The download of '{package.Id}' failed: {ex.Message}", ex)
                : new StoreException(StoreError.IoError, $"Saving '{package.Id}' failed: {ex.Message}", ex);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    /// <summary>GET with redirects followed by hand, each one checked against the policy.</summary>
    private async Task<HttpResponseMessage> GetAsync(Uri url, bool package, CancellationToken cancellationToken)
    {
        for (var hop = 0; ; hop++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new StoreException(StoreError.Offline, $"Could not reach {url.Host}: {ex.Message}", ex);
            }

            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (!_policy.IsAllowedRedirect(next, package))
                {
                    throw new StoreException(StoreError.UrlNotAllowed, $"A redirect to '{next.Scheme}://{next.Host}' is not allowed.");
                }

                if (hop >= MaxRedirects)
                {
                    throw new StoreException(StoreError.HttpError, $"Too many redirects for {url.Host}.");
                }

                url = next;
                continue;
            }

            if (status is < 200 or >= 300)
            {
                response.Dispose();
                throw new StoreException(StoreError.HttpError, $"{url.Host} answered HTTP {status}.");
            }

            return response;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .part file is overwritten by the next download.
        }
    }
}
