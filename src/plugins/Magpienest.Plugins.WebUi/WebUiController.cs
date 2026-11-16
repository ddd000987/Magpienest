using System.Net;
using System.Security.Cryptography.X509Certificates;
using Magpienest.Sdk;

namespace Magpienest.Plugins.WebUi;

/// <summary>Where the server stands, for the settings page.</summary>
internal enum WebServerState
{
    /// <summary>Not listening (starting, restarting or after deactivation).</summary>
    Stopped,

    /// <summary>Listening on <see cref="WebUiController.Port"/>.</summary>
    Running,

    /// <summary>The port could not be opened.</summary>
    PortInUse,

    /// <summary>HTTPS is needed but no usable certificate exists (own file missing or wrong password).</summary>
    CertificateProblem,
}

/// <summary>
/// Owns the running server: starts it, restarts it after a settings change and stops it. All methods may be called
/// from any thread; <see cref="StateChanged"/> is raised on the thread that made the change.
/// </summary>
internal sealed class WebUiController : IAsyncDisposable
{
    /// <summary>A stopped server may hold its port for a moment; a start is retried this many times.</summary>
    private const int StartAttempts = 5;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly WebUiSettings _settings;
    private readonly IWebUiBackend _backend;
    private readonly ILog _log;
    private readonly LibraryApi? _library;
    private readonly string _wwwRoot;
    private readonly int? _portOverride;
    private readonly CertificateStore? _certificates;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebUiServer? _server;
    private X509Certificate2? _certificate;
    private bool _disposed;

    public WebUiController(WebUiSettings settings, IWebUiBackend backend, WebEventHub events, ILog log, string wwwRoot, int? portOverride,
                           SessionStore? sessions = null, LoginThrottle? throttle = null, CertificateStore? certificates = null,
                           LibraryApi? library = null)
    {
        _library = library;
        _settings = settings;
        _backend = backend;
        Events = events;
        _log = log;
        _wwwRoot = wwwRoot;
        _portOverride = portOverride;
        Sessions = sessions;
        Throttle = throttle;
        _certificates = certificates;
    }

    /// <summary>Raised after <see cref="State"/> or <see cref="Port"/> changed.</summary>
    public event Action? StateChanged;

    /// <summary>Raised when too many wrong passwords paused every sign-in (any thread).</summary>
    public event Action? SignInPaused;

    /// <summary>Live events for open browser tabs.</summary>
    public WebEventHub Events { get; }

    /// <summary>Password sessions.</summary>
    public SessionStore? Sessions { get; }

    /// <summary>Brute-force limits.</summary>
    public LoginThrottle? Throttle { get; }

    /// <summary>The stored settings.</summary>
    public WebUiSettings Settings => _settings;

    public WebServerState State { get; private set; } = WebServerState.Stopped;

    /// <summary>The port in use (or that failed).</summary>
    public int Port { get; private set; }

    /// <summary>Whether the running server speaks HTTPS.</summary>
    public bool Secure { get; private set; }

    /// <summary>Thumbprint of the certificate the running server presents, or null over HTTP.</summary>
    public string? CertificateThumbprint => Volatile.Read(ref _certificate)?.Thumbprint;

    /// <summary>The mode the running server was started with.</summary>
    public AccessMode Mode { get; private set; }

    /// <summary>Whether the port comes from <c>--webui-port</c> (settings cannot change it for this run).</summary>
    public bool PortOverridden => _portOverride is not null;

    /// <summary>The address of the web interface on this PC, without the token.</summary>
    public string BaseAddress => Address("127.0.0.1");

    /// <summary>The entry link with the token (secret).</summary>
    public string EntryLink => BaseAddress + "?token=" + _settings.Token;

    /// <summary>The web interface's address through a given host or IP address.</summary>
    /// <param name="host">Host name or IPv4 address.</param>
    public string Address(string host) => $"{(Secure ? "https" : "http")}://{host}:{Port}/";

    /// <summary>Starts (or restarts) the server with the current settings.</summary>
    public async Task<WebServerState> StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return WebServerState.Stopped;
            }

            await StopCoreAsync();
            var port = _portOverride ?? _settings.Port;
            var mode = _settings.Access;
            X509Certificate2? certificate = null;
            if (_settings.EffectiveHttps != HttpsMode.Off)
            {
                certificate = _certificates?.Current(NetworkLinks.CertificateNames(_settings.TailscaleIsHome, _settings.ExtraHosts));
                if (certificate is null)
                {
                    _log.Warn("Web interface: HTTPS is on but no usable certificate was found.");
                    SetState(WebServerState.CertificateProblem, port, mode, secure: true);
                    return State;
                }
            }

            var options = new WebUiHandlerOptions
            {
                WwwRoot = _wwwRoot,
                Token = () => _settings.Token,
                Access = () => new AccessSettings(_settings.Access, _settings.RequirePasswordAtHome, _settings.TailscaleIsHome,
                                                  _settings.ExtraHosts, _settings.PasswordHash),
                Sessions = Sessions,
                Throttle = Throttle,
                SignInPaused = () => SignInPaused?.Invoke(),
                Library = _library,
            };

            foreach (var bindings in BindingChoices(mode, (ushort)port))
            {
                for (var attempt = 1; attempt <= StartAttempts; attempt++)
                {
                    var server = new WebUiServer(bindings, options, _backend, Events, _log, certificate);
                    if (await server.StartAsync())
                    {
                        _server = server;
                        _certificate = certificate;
                        _log.Info($"Web interface listening on {string.Join(", ", bindings.Select(b => $"{b.Address}:{b.Port}"))}" +
                                  $" ({mode}, {(certificate is null ? "HTTP" : "HTTPS")}).");
                        SetState(WebServerState.Running, port, mode, certificate is not null);
                        return State;
                    }

                    await server.DisposeAsync();
                    if (attempt < StartAttempts)
                    {
                        await Task.Delay(RetryDelay);
                    }
                }
            }

            certificate?.Dispose();
            SetState(WebServerState.PortInUse, port, mode, certificate is not null);
            return State;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stores a new port and restarts the server on it.</summary>
    /// <param name="port">A valid port.</param>
    public Task<WebServerState> ChangePortAsync(int port)
    {
        _settings.Port = port;
        return StartAsync();
    }

    /// <summary>Makes a new token; open tabs and copied links stop working.</summary>
    public void ResetToken()
    {
        _settings.ResetToken();
        _log.Info("Web interface access link was reset.");
    }

    /// <summary>Stores a new password (hash) and signs every device out. Slow: call it off the UI thread.</summary>
    /// <param name="password">The new password.</param>
    public void SetPassword(string password)
    {
        _settings.PasswordHash = PasswordHasher.Hash(password);
        Sessions?.Clear();
        _log.Info("Web interface password was changed; all devices were signed out.");
    }

    /// <summary>Makes a new automatic certificate and restarts the server with it.</summary>
    public Task<WebServerState> RenewCertificateAsync()
    {
        _certificates?.CreateAutomatic(NetworkLinks.CertificateNames(_settings.TailscaleIsHome, _settings.ExtraHosts));
        return StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Listening addresses per mode, best first: dual-stack IPv6 covers both families; IPv4 only if IPv6 is off.</summary>
    private static IEnumerable<IReadOnlyList<WebBinding>> BindingChoices(AccessMode mode, ushort port)
    {
        if (mode == AccessMode.Local)
        {
            yield return [new WebBinding(IPAddress.Loopback, port)];
            yield break;
        }

        yield return [new WebBinding(IPAddress.IPv6Any, port, DualStack: true)];
        yield return [new WebBinding(IPAddress.Any, port)];
    }

    private async Task StopCoreAsync()
    {
        if (_server is { } server)
        {
            _server = null;
            await server.DisposeAsync();
            _certificate?.Dispose();
            _certificate = null;
            SetState(WebServerState.Stopped, Port, Mode, Secure);
        }
    }

    private void SetState(WebServerState state, int port, AccessMode mode, bool secure)
    {
        State = state;
        Port = port;
        Mode = mode;
        Secure = secure;
        StateChanged?.Invoke();
    }
}
