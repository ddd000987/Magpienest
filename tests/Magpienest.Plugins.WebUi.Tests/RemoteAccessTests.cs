using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Magpienest.Plugins.WebUi;

namespace Magpienest.Plugins.WebUi.Tests;

/// <summary>
/// Remote access over a real server: requests come from loopback, and the classifier is replaced to make them count as home
/// network or internet.
/// </summary>
public sealed class RemoteAccessTests : IAsyncDisposable
{
    private const string Token = "local-token-0123456789";
    private const string Password = "correct horse battery";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mn-webui-remote-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private readonly List<WebUiServer> _servers = [];
    private readonly List<HttpClient> _clients = [];
    private readonly string _hash = PasswordHasher.Hash(Password, iterations: 1000);
    private SessionStore? _sessions;
    private string _base = "";

    public RemoteAccessTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "www", "js"));
        File.WriteAllText(Path.Combine(_dir, "www", "index.html"), "<!doctype html><title></title>");
        File.WriteAllText(Path.Combine(_dir, "www", "login.html"), "<!doctype html><title>login</title>");
        File.WriteAllText(Path.Combine(_dir, "www", "app.css"), "body{}");
        File.WriteAllText(Path.Combine(_dir, "www", "js", "login.js"), "");
        File.WriteAllText(Path.Combine(_dir, "www", "js", "main.js"), "");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        foreach (var server in _servers)
        {
            await server.DisposeAsync();
        }

        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Home_device_signs_in_uses_the_session_and_signs_out()
    {
        var client = await StartAsync(AccessMode.Home, AddressClass.Home, requireAtHome: true);
        var ct = TestContext.Current.CancellationToken;

        using (var page = await client.GetAsync(_base + "/", ct))
        {
            Assert.Equal(HttpStatusCode.Found, page.StatusCode);
            Assert.Equal("/login", page.Headers.Location?.OriginalString);
        }

        using (var login = await client.GetAsync(_base + "/login", ct))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            Assert.Contains("login", await login.Content.ReadAsStringAsync(ct));
        }

        using (var css = await client.GetAsync(_base + "/js/login.js", ct))
        {
            Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        }

        using (var main = await client.GetAsync(_base + "/js/main.js", ct))
        {
            Assert.Equal(HttpStatusCode.Found, main.StatusCode);
        }

        await AssertCodeAsync(await client.GetAsync(_base + "/api/state", ct), HttpStatusCode.Unauthorized, "signIn");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(_base + "/api/strings", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(_base + "/api/appearance", ct)).StatusCode);

        await AssertCodeAsync(await LoginAsync(client, "wrong password"), HttpStatusCode.Unauthorized, "wrongPassword");

        using var ok = await LoginAsync(client, Password);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var setCookie = Assert.Single(ok.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(WebUiHandler.SessionCookie + "=", setCookie);
        Assert.Contains("HttpOnly", setCookie);
        Assert.Contains("SameSite=Strict", setCookie);
        Assert.Contains("Max-Age=2592000", setCookie);
        var session = setCookie.Split(';')[0].Split('=', 2)[1];

        using (var state = await SendAsync(client, HttpMethod.Get, "/api/state", session))
        {
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
            using var json = JsonDocument.Parse(await state.Content.ReadAsStringAsync(ct));
            Assert.True(json.RootElement.GetProperty("canLogout").GetBoolean());
        }

        // A signed-in device is sent away from the sign-in page.
        using (var again = await SendAsync(client, HttpMethod.Get, "/login", session))
        {
            Assert.Equal(HttpStatusCode.Found, again.StatusCode);
        }

        var device = Assert.Single(_sessions!.List());
        Assert.Equal("127.0.0.1", device.LastAddress);

        using (var logout = await SendAsync(client, HttpMethod.Post, "/api/logout", session, origin: true))
        {
            Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
            Assert.Contains("Max-Age=0", Assert.Single(logout.Headers.GetValues("Set-Cookie")));
        }

        await AssertCodeAsync(await SendAsync(client, HttpMethod.Get, "/api/state", session), HttpStatusCode.Unauthorized, "signIn");
        Assert.Empty(_sessions.List());
    }

    [Fact]
    public async Task Home_without_password_lets_devices_in_but_checks_the_host()
    {
        var client = await StartAsync(AccessMode.Home, AddressClass.Home, requireAtHome: false);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(_base + "/api/state", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(_base + "/", ct)).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, _base + "/api/state");
        request.Headers.Host = "rebind.evil.example";
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_access_token_does_not_replace_the_password_from_another_device()
    {
        var client = await StartAsync(AccessMode.Internet, AddressClass.Internet, requireAtHome: true);
        var ct = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Get, _base + "/api/state");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        await AssertCodeAsync(await client.SendAsync(request, ct), HttpStatusCode.Unauthorized, "signIn");

        using var link = await client.GetAsync(_base + "/?token=" + Token, ct);
        Assert.Equal(HttpStatusCode.Found, link.StatusCode);
        Assert.Equal("/login", link.Headers.Location?.OriginalString);
        Assert.False(link.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("Local", "Home")]
    [InlineData("Local", "Internet")]
    [InlineData("Home", "Internet")]
    public async Task Requests_outside_the_mode_are_dropped(string mode, string from)
    {
        var client = await StartAsync(Enum.Parse<AccessMode>(mode), Enum.Parse<AddressClass>(from), requireAtHome: true);
        var ct = TestContext.Current.CancellationToken;
        foreach (var path in new[] { "/", "/login", "/api/strings", "/app.css" })
        {
            using var response = await client.GetAsync(_base + path, ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
            Assert.True(response.Headers.ConnectionClose);
        }
    }

    [Fact]
    public async Task Forwarded_for_header_does_not_change_where_a_request_comes_from()
    {
        var client = await StartAsync(AccessMode.Home, AddressClass.Internet, requireAtHome: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, _base + "/api/state");
        request.Headers.Add("X-Forwarded-For", "192.168.1.2");
        request.Headers.Add("Forwarded", "for=192.168.1.2");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.ConnectionClose);
    }

    [Fact]
    public async Task Wrong_passwords_are_throttled()
    {
        var client = await StartAsync(AccessMode.Internet, AddressClass.Internet, requireAtHome: true);
        for (var i = 0; i < LoginThrottle.PerAddressLimit; i++)
        {
            await AssertCodeAsync(await LoginAsync(client, "nope nope"), HttpStatusCode.Unauthorized, "wrongPassword");
        }

        // Even the right password waits now.
        using var blocked = await LoginAsync(client, Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(TimeSpan.FromMinutes(15), blocked.Headers.RetryAfter?.Delta);
        await AssertCodeAsync(blocked, HttpStatusCode.TooManyRequests, "tooManyAttempts");

        _clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, Password)).StatusCode);
    }

    [Fact]
    public async Task Sign_in_needs_the_same_origin_and_a_password_that_exists()
    {
        var client = await StartAsync(AccessMode.Home, AddressClass.Home, requireAtHome: true);
        var ct = TestContext.Current.CancellationToken;
        using (var request = new HttpRequestMessage(HttpMethod.Post, _base + "/api/login"))
        {
            request.Content = new StringContent(JsonSerializer.Serialize(new { password = Password }), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await AssertCodeAsync(await LoginRawAsync(client, "{ not json"), HttpStatusCode.BadRequest, "badRequest");
        await AssertCodeAsync(await LoginRawAsync(client, new string('x', 20_000)), HttpStatusCode.BadRequest, "badRequest");

        var noPassword = await StartAsync(AccessMode.Home, AddressClass.Home, requireAtHome: true, passwordHash: null);
        await AssertCodeAsync(await LoginAsync(noPassword, Password), HttpStatusCode.Forbidden, "noPassword");
    }

    [Fact]
    public async Task Https_server_with_a_generated_certificate()
    {
        var pfx = CertificateStore.CreateSelfSigned(["localhost", "127.0.0.1"], "pw", DateTimeOffset.UtcNow);
        var certificate = X509CertificateLoader.LoadPkcs12(pfx, "pw", X509KeyStorageFlags.UserKeySet);
        var thumbprint = certificate.Thumbprint;
        var client = await StartAsync(AccessMode.Local, AddressClass.Loopback, requireAtHome: true, certificate: certificate);
        var ct = TestContext.Current.CancellationToken;
        Assert.StartsWith("https://", _base);

        using (var request = new HttpRequestMessage(HttpMethod.Get, _base + "/api/state"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var link = await client.GetAsync(_base + "/?token=" + Token, ct);
        Assert.Contains("; Secure", Assert.Single(link.Headers.GetValues("Set-Cookie")));
        Assert.Equal(thumbprint, _lastServerThumbprint);
    }

    private string? _lastServerThumbprint;

    private async Task<HttpClient> StartAsync(AccessMode mode, AddressClass from, bool requireAtHome, string? passwordHash = "",
                                              X509Certificate2? certificate = null)
    {
        passwordHash = passwordHash == "" ? _hash : passwordHash;
        _sessions = new SessionStore(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"), _clock);
        var port = WebUiServer.FreePort();
        var server = new WebUiServer(
            [new WebBinding(IPAddress.Loopback, port)],
            new WebUiHandlerOptions
            {
                WwwRoot = Path.Combine(_dir, "www"),
                Token = () => Token,
                Access = () => new AccessSettings(mode, requireAtHome, true, [], passwordHash),
                Sessions = _sessions,
                Throttle = new LoginThrottle(_clock),
                MachineName = "TESTPC",
                Classify = (_, _) => from,
            },
            new StaticBackend(), new WebEventHub(), new NullLog(), certificate);
        _servers.Add(server);
        Assert.True(await server.StartAsync());
        _base = $"{(certificate is null ? "http" : "https")}://127.0.0.1:{port}";

        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        if (certificate is not null)
        {
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                _lastServerThumbprint = cert?.Thumbprint;
                return true;
            };
        }

        var client = new HttpClient(handler);
        _clients.Add(client);
        return client;
    }

    private Task<HttpResponseMessage> LoginAsync(HttpClient client, string password) =>
        LoginRawAsync(client, JsonSerializer.Serialize(new { password }));

    private async Task<HttpResponseMessage> LoginRawAsync(HttpClient client, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _base + "/api/login");
        request.Headers.Add("Origin", _base);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string session, bool origin = false)
    {
        using var request = new HttpRequestMessage(method, _base + path);
        request.Headers.Add("Cookie", WebUiHandler.SessionCookie + "=" + session);
        if (origin)
        {
            request.Headers.Add("Origin", _base);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task AssertCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        }
    }

    private sealed class StaticBackend : IWebUiBackend
    {
        public Task<WebState> GetStateAsync() => Task.FromResult(new WebState("en", "light", []));

        public Task<IReadOnlyDictionary<string, string>> GetStringsAsync() =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }
}
