using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Magpienest.Plugins.Catalogue;
using Magpienest.Sdk;

namespace Magpienest.Plugins.Catalogue.Tests;

/// <summary>A minimal HTTP/1.1 server on 127.0.0.1 (free port, no URL reservation needed). Never touches the network.</summary>
internal sealed class TestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<string, Func<Response?>> _routes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public TestServer()
    {
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string BaseUrl { get; }

    public ConcurrentQueue<Request> Requests { get; } = new();

    public StorePolicy Policy => new(
        new Uri(BaseUrl + "catalog.json"),
        BaseUrl + "releases/",
        RequireHttps: false,
        new[] { "127.0.0.1" }.ToFrozenSet(),
        new[] { "127.0.0.1" }.ToFrozenSet());

    /// <summary>Serves <paramref name="body"/>; <paramref name="contentLength"/> -1 sends no Content-Length header.</summary>
    public void Serve(string path, byte[] body, int status = 200, long? contentLength = null) =>
        _routes[path] = () => new Response(status, body, null, contentLength);

    public void Serve(string path, string body) => Serve(path, Encoding.UTF8.GetBytes(body));

    public void Redirect(string path, string location, int status = 302) =>
        _routes[path] = () => new Response(status, [], location, null);

    /// <summary>Accepts the request for <paramref name="path"/> and never answers.</summary>
    public void Silent(string path) => _routes[path] = () => null;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream);
                var lines = head.Split("\r\n");
                var path = lines[0].Split(' ')[1];
                var headers = lines.Skip(1).Where(l => l.Contains(':'))
                    .ToDictionary(l => l[..l.IndexOf(':')].Trim(), l => l[(l.IndexOf(':') + 1)..].Trim(), StringComparer.OrdinalIgnoreCase);
                Requests.Enqueue(new Request(path, headers));

                var response = _routes.TryGetValue(path, out var route) ? route() : new Response(404, [], null, null);
                if (response is null)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
                    return;
                }

                var text = new StringBuilder();
                text.Append($"HTTP/1.1 {response.Status} X\r\n");
                if (response.ContentLength != -1)
                {
                    text.Append($"Content-Length: {response.ContentLength ?? response.Body.Length}\r\n");
                }
                if (response.Location is not null)
                {
                    text.Append($"Location: {response.Location}\r\n");
                }

                text.Append("Connection: close\r\n\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(text.ToString()));
                await stream.WriteAsync(response.Body);
                await stream.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // The client went away.
            }
        }
    }

    private static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1)
        {
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n')
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    internal sealed record Request(string Path, IReadOnlyDictionary<string, string> Headers);

    private sealed record Response(int Status, byte[] Body, string? Location, long? ContentLength);
}

/// <summary>Records what the installer does with the kernel.</summary>
internal sealed class FakePluginManager : IPluginManager
{
    public List<PluginInfo> Installed { get; } = [];

    public List<string> InstallCalls { get; } = [];

    public List<bool> FileExistedAtInstall { get; } = [];

    public List<string> Uninstalled { get; } = [];

    /// <summary>Package path → result; by default the plugin id is read from the file name ("id-version.zip").</summary>
    public Func<string, PluginInstallResult>? Result { get; set; }

    public IReadOnlyList<PluginInfo> Plugins => Installed;

    public bool RestartRequired => false;

    public void SetEnabled(string pluginId, bool enabled) => throw new NotSupportedException();

    /// <summary>Package path → inspection; by default valid, with id and version read from the file name.</summary>
    public Func<string, PluginPackageInfo>? Inspection { get; set; }

    public List<string> InspectCalls { get; } = [];

    public Task<PluginPackageInfo> InspectAsync(string zipOrFolderPath)
    {
        InspectCalls.Add(zipOrFolderPath);
        return Task.FromResult(Inspection?.Invoke(zipOrFolderPath)
                               ?? new PluginPackageInfo(true, new PluginManifest { Id = IdOf(zipOrFolderPath), Version = VersionOf(zipOrFolderPath), Assembly = "T.dll" }, null, null));
    }

    public static string VersionOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name[(name.LastIndexOf('-') + 1)..];
    }

    public Task<PluginInstallResult> InstallAsync(string zipOrFolderPath)
    {
        InstallCalls.Add(zipOrFolderPath);
        FileExistedAtInstall.Add(File.Exists(zipOrFolderPath));
        var result = Result?.Invoke(zipOrFolderPath) ?? new PluginInstallResult(true, IdOf(zipOrFolderPath), false, null);
        if (result.Success && result.PluginId is { } id && Installed.All(p => p.Manifest.Id != id))
        {
            Installed.Add(Plugin(id, "1.0.0", builtIn: false));
        }

        return Task.FromResult(result);
    }

    public void Uninstall(string pluginId) => Uninstalled.Add(pluginId);

    public void UndoUninstall(string pluginId) => throw new NotSupportedException();

    public static string IdOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name[..name.LastIndexOf('-')];
    }

    public static PluginInfo Plugin(string id, string version, bool builtIn = false, string? bundle = null, PluginState state = PluginState.Active) =>
        new(new PluginManifest { Id = id, Version = version, Assembly = "T.dll", Bundle = bundle }, state, true, builtIn, null, []);
}

/// <summary>Writes a fake zip per package; can fail one package.</summary>
internal sealed class FakeDownloader(string? failId = null, StoreError failure = StoreError.HashMismatch) : IPackageDownloader
{
    public List<string> Downloaded { get; } = [];

    public Func<CataloguePackage, CancellationToken, Task>? BeforeDownload { get; set; }

    public async Task<string> DownloadAsync(CataloguePackage package, string version, string directory, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (BeforeDownload is not null)
        {
            await BeforeDownload(package, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (package.Id == failId)
        {
            throw new StoreException(failure, "test failure");
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{package.Id}-{version}.zip");
        await File.WriteAllBytesAsync(path, [1, 2, 3], cancellationToken);
        progress?.Report(package.Size);
        Downloaded.Add(package.Id);
        return path;
    }
}

internal sealed class NullLog : ILog
{
    public List<string> Warnings { get; } = [];

    public void Debug(string message) { }

    public void Info(string message) { }

    public void Warn(string message, Exception? ex = null) => Warnings.Add(message);

    public void Error(string message, Exception? ex = null) => Warnings.Add(message);
}

internal static class Sample
{
    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static CataloguePackage Package(string id, long size = 3, string url = "https://github.com/o/s/releases/download/x/p.zip", string? sha = null) =>
        new(id, size, new Uri(url), sha ?? new string('a', 64));

    public static CatalogueBundle Bundle(string id, string version = "1.0.0", int apiVersion = 1, params CataloguePackage[] packages) =>
        new(id, new Dictionary<string, string> { ["en"] = id }, new Dictionary<string, string>(), "Magpienest", version, apiVersion,
            packages.Length == 0 ? [Package(id)] : packages);

    public static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "magpienest-catalogue-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
