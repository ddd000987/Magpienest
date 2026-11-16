using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Magpienest.Plugins.WebUi;
using Magpienest.Sdk;

namespace Magpienest.Plugins.WebUi.Tests;

/// <summary>A real server on a free loopback port.</summary>
public sealed class ServerTests : IAsyncLifetime
{
    private const string Token = "test-token-0123456789";

    private readonly string _www = Path.Combine(Path.GetTempPath(), "mn-webui-www-" + Guid.NewGuid().ToString("N"));
    private readonly WebEventHub _events = new();
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private WebUiServer? _server;
    private ushort _port;

    private string Base => $"http://127.0.0.1:{_port}";

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_www, "js"));
        await File.WriteAllTextAsync(Path.Combine(_www, "index.html"), "<!doctype html><title></title>");
        await File.WriteAllTextAsync(Path.Combine(_www, "app.css"), "body{}");
        await File.WriteAllTextAsync(Path.Combine(_www, "js", "main.js"), "export {};");
        await File.WriteAllTextAsync(Path.Combine(_www, "logo.svg"), "<svg/>");
        _port = WebUiServer.FreePort();
        _server = Create(_port);
        Assert.True(await _server.StartAsync());
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        Directory.Delete(_www, recursive: true);
    }

    [Fact]
    public async Task Wrong_host_is_forbidden()
    {
        using var request = Authorized(HttpMethod.Get, "/api/state");
        request.Headers.Host = "evil.example.com";
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Localhost_host_is_accepted()
    {
        using var request = Authorized(HttpMethod.Get, "/api/state");
        request.Headers.Host = $"localhost:{_port}";
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task No_token_is_unauthorized()
    {
        var ct = TestContext.Current.CancellationToken;
        using var api = await _client.GetAsync(Base + "/api/state", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal("application/json", api.Content.Headers.ContentType?.MediaType);

        using var page = await _client.GetAsync(Base + "/", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        var html = await page.Content.ReadAsStringAsync(ct);
        Assert.Contains("Open it from the app", html);
        Assert.Contains("lang=\"zh-TW\"", html);

        using var file = await _client.GetAsync(Base + "/js/main.js", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, file.StatusCode);
    }

    [Fact]
    public async Task Wrong_token_cookie_or_link_is_unauthorized()
    {
        var ct = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Get, Base + "/api/state");
        request.Headers.Add("Cookie", WebUiHandler.TokenCookie + "=nope");
        using var response = await _client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var link = await _client.GetAsync(Base + "/?token=nope", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, link.StatusCode);
        Assert.False(link.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Token_link_sets_the_cookie_and_redirects()
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await _client.GetAsync(Base + "/?token=" + Token, ct);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(WebUiHandler.TokenCookie + "=" + Token + ";", cookie);
        Assert.Contains("HttpOnly", cookie);
        Assert.Contains("SameSite=Strict", cookie);
        Assert.Contains("Path=/", cookie);
        Assert.DoesNotContain("Secure", cookie);

        // The cookie then opens everything.
        using var request = new HttpRequestMessage(HttpMethod.Get, Base + "/api/state");
        request.Headers.Add("Cookie", WebUiHandler.TokenCookie + "=" + Token);
        using var state = await _client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);
    }

    [Fact]
    public async Task State_and_strings()
    {
        var ct = TestContext.Current.CancellationToken;
        using var request = Authorized(HttpMethod.Get, "/api/state");
        using var response = await _client.SendAsync(request, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("zh-TW", json.RootElement.GetProperty("language").GetString());
        Assert.Equal("dark", json.RootElement.GetProperty("theme").GetString());
        var kind = Assert.Single(json.RootElement.GetProperty("kinds").EnumerateArray());
        Assert.Equal("comic", kind.GetProperty("id").GetString());
        Assert.Equal("Comics", kind.GetProperty("name").GetString());

        // Strings need no token (the sign-in page uses them).
        using var strings = await _client.GetAsync(Base + "/api/strings", ct);
        Assert.Equal(HttpStatusCode.OK, strings.StatusCode);
        using var texts = JsonDocument.Parse(await strings.Content.ReadAsStringAsync(ct));
        Assert.Equal("Open it from the app", texts.RootElement.GetProperty("WebUi.Web.OpenFromApp").GetString());
    }

    [Theory]
    [InlineData("/", "text/html")]
    [InlineData("/index.html", "text/html")]
    [InlineData("/app.css", "text/css")]
    [InlineData("/js/main.js", "text/javascript")]
    public async Task Static_files_have_their_content_types(string path, string type)
    {
        using var request = Authorized(HttpMethod.Get, path);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/logo.svg")]
    [InlineData("/missing.js")]
    [InlineData("/api/nothing")]
    public async Task Unknown_or_refused_paths_are_not_found(string path)
    {
        using var request = Authorized(HttpMethod.Get, path);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Every_response_carries_the_security_headers()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var path in new[] { "/", "/api/state", "/api/nothing" })
        {
            using var request = Authorized(HttpMethod.Get, path);
            using var response = await _client.SendAsync(request, ct);
            Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
            Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
            Assert.Equal("DENY", Header(response, "X-Frame-Options"));
            Assert.Contains("default-src 'self'", Header(response, "Content-Security-Policy"));
            Assert.Equal("no-store", Header(response, "Cache-Control"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        }

        using var unauthorized = await _client.GetAsync(Base + "/api/state", ct);
        Assert.Equal("nosniff", Header(unauthorized, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task Writes_need_the_same_origin()
    {
        var ct = TestContext.Current.CancellationToken;
        using (var request = Authorized(HttpMethod.Patch, "/api/state"))
        {
            request.Content = new StringContent("{}");
            using var response = await _client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var request = Authorized(HttpMethod.Patch, "/api/state"))
        {
            request.Headers.Add("Origin", "https://evil.example.com");
            request.Content = new StringContent("{}");
            using var response = await _client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // Same origin passes the guard (and then finds no such write endpoint).
        using (var request = Authorized(HttpMethod.Patch, "/api/state"))
        {
            request.Headers.Add("Origin", Base);
            request.Content = new StringContent("{}");
            using var response = await _client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Events_stream_delivers_published_events()
    {
        var ct = TestContext.Current.CancellationToken;
        using var request = Authorized(HttpMethod.Get, "/api/events");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        Assert.Equal(": connected", await reader.ReadLineAsync(ct));

        // The subscription exists once the first line arrived.
        _events.Publish("language", new { language = "en" });
        string? line;
        do
        {
            line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        while (line == "");

        Assert.Equal("event: language", line);
        Assert.Equal("data: {\"language\":\"en\"}", await reader.ReadLineAsync(ct));
    }

    [Fact]
    public async Task Port_in_use_is_reported_not_thrown()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = (ushort)((IPEndPoint)blocker.LocalEndpoint).Port;
        await using var server = Create(port);
        Assert.False(await server.StartAsync());
    }

    [Fact]
    public async Task Controller_reports_port_in_use_and_recovers_on_a_new_port()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busy = ((IPEndPoint)blocker.LocalEndpoint).Port;
        var config = new MemoryConfig();
        var settings = new WebUiSettings(config) { Port = busy };
        await using var controller = new WebUiController(settings, new FakeBackend(), _events, new NullLog(), _www, portOverride: null);

        Assert.Equal(WebServerState.PortInUse, await controller.StartAsync());
        Assert.Equal(busy, controller.Port);

        var free = WebUiServer.FreePort();
        Assert.Equal(WebServerState.Running, await controller.ChangePortAsync(free));
        Assert.Equal(free, settings.Port);
        Assert.Equal($"http://127.0.0.1:{free}/", controller.BaseAddress);
        Assert.Equal($"http://127.0.0.1:{free}/?token={settings.Token}", controller.EntryLink);

        // A reset token takes effect without a restart.
        var old = settings.Token;
        controller.ResetToken();
        Assert.NotEqual(old, settings.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{free}/api/state");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", old);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private WebUiServer Create(ushort port) =>
        new([new WebBinding(IPAddress.Loopback, port)],
            new WebUiHandlerOptions { WwwRoot = _www, Token = () => Token, MachineName = "TESTPC" },
            new FakeBackend(), _events, new NullLog());

    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, Base + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values)
            : "";

    private sealed class FakeBackend : IWebUiBackend
    {
        public Task<WebState> GetStateAsync() =>
            Task.FromResult(new WebState("zh-TW", "dark", [new WebKind("comic", "Comics")]));

        public Task<IReadOnlyDictionary<string, string>> GetStringsAsync() =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
            {
                ["WebUi.Web.AppName"] = "Magpienest",
                ["WebUi.Web.OpenFromApp"] = "Open it from the app",
            });
    }
}

/// <summary>An in-memory plugin config.</summary>
internal sealed class MemoryConfig : IPluginConfig
{
    private readonly Dictionary<string, object?> _values = [];

    public T Get<T>(string key, T defaultValue) => _values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

    public void Set<T>(string key, T value) => _values[key] = value;
}

/// <summary>A log that discards everything.</summary>
internal sealed class NullLog : ILog
{
    public void Debug(string message)
    {
    }

    public void Info(string message)
    {
    }

    public void Warn(string message, Exception? ex = null)
    {
    }

    public void Error(string message, Exception? ex = null)
    {
    }
}
