using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using GenHTTP.Api.Infrastructure;
using Magpienest.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace Magpienest.Plugins.WebUi;

/// <summary>Where the server listens.</summary>
/// <param name="Address">Address to bind.</param>
/// <param name="Port">TCP port.</param>
/// <param name="DualStack">Whether an IPv6 address also accepts IPv4 connections.</param>
internal sealed record WebBinding(IPAddress Address, ushort Port, bool DualStack = false);

/// <summary>One running GenHTTP server. Create, <see cref="StartAsync"/>, and dispose to stop.</summary>
internal sealed class WebUiServer : IAsyncDisposable
{
    private readonly IServerHost _host;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ILog _log;
    private bool _started;

    public WebUiServer(IReadOnlyList<WebBinding> bindings, WebUiHandlerOptions options, IWebUiBackend backend,
                       WebEventHub events, ILog log, X509Certificate2? certificate = null)
    {
        _log = log;
        Bindings = bindings;
        Secure = certificate is not null;
        var handler = new WebUiHandler(options, backend, events, log, _stopping.Token);
        _host = GenHTTP.Engine.Internal.Host.Create()
            .Handler(handler)
            .Logging(NullLoggerFactory.Instance, false);
        foreach (var binding in bindings)
        {
            if (certificate is null)
            {
                _host.Bind(binding.Address, binding.Port, HttpProtocols.Http1, binding.DualStack);
            }
            else
            {
                _host.Bind(binding.Address, binding.Port, new FixedCertificate(certificate), SslProtocols.Tls12 | SslProtocols.Tls13,
                           null!, HttpProtocols.Http1, binding.DualStack);
            }
        }
    }

    /// <summary>Whether the server speaks HTTPS.</summary>
    public bool Secure { get; }

    /// <summary>The addresses and ports this server listens on.</summary>
    public IReadOnlyList<WebBinding> Bindings { get; }

    /// <summary>Opens the ports. Returns false (and logs) when a port cannot be opened, for example because it is in use.</summary>
    public async Task<bool> StartAsync()
    {
        try
        {
            await _host.StartAsync();
            _started = true;
            return true;
        }
        catch (Exception ex) when (IsBindFailure(ex))
        {
            _log.Warn($"Web interface: could not listen on {string.Join(", ", Bindings.Select(b => $"{b.Address}:{b.Port}"))}: {ex.Message}");
            await StopHostAsync();
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_started)
        {
            await StopHostAsync();
        }

        _stopping.Dispose();
    }

    /// <summary>Whether a free port can be bound on loopback right now (for tests and the CLI).</summary>
    public static ushort FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return (ushort)((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static bool IsBindFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException or IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        // GenHTTP wraps the socket error in its own exception type.
        return ex.GetType().Name.Contains("Binding", StringComparison.Ordinal);
    }

    private async Task StopHostAsync()
    {
        try
        {
            await _host.StopAsync();
        }
        catch (Exception ex)
        {
            _log.Warn("Web interface: error while stopping the server: " + ex.Message);
        }
    }

    private sealed class FixedCertificate(X509Certificate2 certificate) : ICertificateProvider
    {
        public X509Certificate2? Provide(string? host) => certificate;
    }
}
