using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using GenHTTP.Api.Content;
using GenHTTP.Api.Infrastructure;
using GenHTTP.Api.Protocol;
using Magpienest.Sdk;

namespace Magpienest.Plugins.WebUi;

/// <summary>The access settings a request is judged by; read for every request so changes apply at once.</summary>
/// <param name="Mode">Who can connect.</param>
/// <param name="RequirePasswordAtHome">Whether home-network devices must sign in.</param>
/// <param name="TailscaleIsHome">Whether Tailscale addresses count as home network.</param>
/// <param name="ExtraHosts">Extra allowed host names.</param>
/// <param name="PasswordHash">The password hash, or null.</param>
internal sealed record AccessSettings(AccessMode Mode, bool RequirePasswordAtHome, bool TailscaleIsHome,
                                      IReadOnlyCollection<string> ExtraHosts, string? PasswordHash)
{
    /// <summary>This PC only, no password.</summary>
    public static AccessSettings LocalOnly { get; } = new(AccessMode.Local, true, true, [], null);
}

/// <summary>What the handler needs to know about the running server.</summary>
internal sealed record WebUiHandlerOptions
{
    /// <summary>The plugin's <c>www</c> folder.</summary>
    public required string WwwRoot { get; init; }

    /// <summary>The current access token (it changes when the user resets the link).</summary>
    public required Func<string> Token { get; init; }

    /// <summary>The current access settings.</summary>
    public Func<AccessSettings> Access { get; init; } = () => AccessSettings.LocalOnly;

    /// <summary>Password sessions (null: nobody can sign in).</summary>
    public SessionStore? Sessions { get; init; }

    /// <summary>Brute-force limits (null: none, tests only).</summary>
    public LoginThrottle? Throttle { get; init; }

    /// <summary>Called when too many wrong passwords paused every sign-in (any thread).</summary>
    public Action? SignInPaused { get; init; }

    /// <summary>This computer's name (an allowed <c>Host</c>).</summary>
    public string MachineName { get; init; } = Environment.MachineName;

    /// <summary>The library endpoints (null: none, as in tests of the access rules).</summary>
    public LibraryApi? Library { get; init; }

    /// <summary>Classifies the remote address (tests replace it to simulate other networks).</summary>
    public Func<IPAddress, bool, AddressClass> Classify { get; init; } = AddressClassifier.Classify;
}

/// <summary>
/// Every request goes through here: the access rules first, then the API and the static files.
/// Runs on the server's threads, never on the UI thread.
/// </summary>
internal sealed class WebUiHandler : IHandler
{
    /// <summary>Cookie that carries the access token (this PC).</summary>
    public const string TokenCookie = "mn_token";

    /// <summary>Cookie that carries a password session.</summary>
    public const string SessionCookie = "mn_session";

    /// <summary>Largest request body accepted (JSON only).</summary>
    private const int MaxBodyBytes = 16 * 1024;

    private const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self'; script-src 'self'; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";

    /// <summary>Pages and files the sign-in page needs; served to everyone the mode lets in.</summary>
    private static readonly HashSet<string> PublicFiles = new(StringComparer.Ordinal) { "/login", "/app.css", "/js/login.js" };

    private readonly WebUiHandlerOptions _options;
    private readonly IWebUiBackend _backend;
    private readonly WebEventHub _events;
    private readonly ILog _log;
    private readonly StaticFiles _files;
    private readonly CancellationToken _stopping;

    public WebUiHandler(WebUiHandlerOptions options, IWebUiBackend backend, WebEventHub events, ILog log, CancellationToken stopping)
    {
        _options = options;
        _backend = backend;
        _events = events;
        _log = log;
        _stopping = stopping;
        _files = new StaticFiles(options.WwwRoot);
    }

    public ValueTask PrepareAsync(IServer server) => ValueTask.CompletedTask;

    public async ValueTask<IResponse?> HandleAsync(IRequest request)
    {
        try
        {
            return await HandleCoreAsync(request);
        }
        catch (Exception ex)
        {
            _log.Error("Web interface request failed: " + PathOf(request), ex);
            return Json(request, ResponseStatus.InternalServerError, new { error = "Internal error" });
        }
    }

    private async Task<IResponse> HandleCoreAsync(IRequest request)
    {
        var headers = request.Header.Headers;
        var host = headers.GetEntry("Host")?.ToString();
        var path = PathOf(request);
        var api = path.StartsWith("/api/", StringComparison.Ordinal);
        var secure = request.EndPoint.Secure;
        var access = _options.Access();
        var address = request.Client.Address ?? IPAddress.None;
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var from = _options.Classify(address, access.TailscaleIsHome);
        var method = request.Header.Method;
        var reading = method.Equals(RequestMethod.Get) || method.Equals(RequestMethod.Head);

        var token = _options.Token();
        var cookies = headers.GetEntry("Cookie")?.ToString();
        var hasToken = RequestGuards.SecretEquals(RequestGuards.Cookie(cookies, TokenCookie), token)
                       || RequestGuards.SecretEquals(RequestGuards.Bearer(headers.GetEntry("Authorization")?.ToString()), token);
        var session = RequestGuards.Cookie(cookies, SessionCookie);
        var hasSession = _options.Sessions?.Validate(session, address.ToString()) ?? false;
        var hostAllowed = RequestGuards.IsAllowedHost(host, _options.MachineName, access.ExtraHosts);
        var facts = new AccessRequest(access.Mode, from, hostAllowed, hasToken, hasSession, access.PasswordHash is not null,
                                      access.RequirePasswordAtHome);
        var decision = AccessPolicy.Decide(facts);

        if (decision == AccessDecision.Drop)
        {
            return request.Respond().Status(ResponseStatus.Forbidden).Connection(Connection.Close).Content(new BytesContent([])).Build();
        }

        if (!reading && !RequestGuards.IsSameOrigin(headers.GetEntry("Origin")?.ToString(), secure, host))
        {
            return Error(request, ResponseStatus.Forbidden, "Cross-site request refused");
        }

        // The entry link from the app: check the token, keep it in a cookie, drop it from the address bar.
        if (path == "/" && request.Header.Query.GetEntry("token")?.ToString() is { } given)
        {
            if (RequestGuards.SecretEquals(given, token) &&
                AccessPolicy.Decide(facts with { HasToken = true }) == AccessDecision.Allow)
            {
                var cookie = $"{TokenCookie}={token}; HttpOnly; SameSite=Strict; Path=/" + (secure ? "; Secure" : "");
                return Secured(request.Respond().Status(ResponseStatus.Found).Header("Location", "/").Header("Set-Cookie", cookie))
                    .Content(new BytesContent([])).Build();
            }

            return await DeniedAsync(request, decision, api: false);
        }

        // What the sign-in page needs is open to everyone the mode lets in.
        switch (path)
        {
            case "/api/strings" when reading:
                return Json(request, ResponseStatus.Ok, await _backend.GetStringsAsync());
            case "/api/appearance" when reading:
                var look = await _backend.GetStateAsync();
                return Json(request, ResponseStatus.Ok, new { look.Language, look.Theme });
            case "/api/login" when method.Equals(RequestMethod.Post):
                return await SignInAsync(request, access, address.ToString(), secure);
            case "/login" when reading && decision == AccessDecision.Allow && hasSession:
                return Redirect(request, "/");
            case "/login" when reading:
                return File(request, "/login.html");
        }

        if (reading && PublicFiles.Contains(path))
        {
            return File(request, path);
        }

        if (decision != AccessDecision.Allow)
        {
            return await DeniedAsync(request, decision, api);
        }

        if (api)
        {
            return await ApiAsync(request, path, method, reading, session, hasSession, secure);
        }

        return reading ? File(request, path) : Error(request, ResponseStatus.MethodNotAllowed, "Method not allowed");
    }

    private async Task<IResponse> ApiAsync(IRequest request, string path, RequestMethod method, bool reading, string? session,
                                           bool hasSession, bool secure)
    {
        switch (path)
        {
            case "/api/state" when reading:
                var state = await _backend.GetStateAsync();
                return Json(request, ResponseStatus.Ok, new { state.Language, state.Theme, state.Kinds, canLogout = hasSession });

            case "/api/events" when reading:
                return Secured(request.Respond().Status(ResponseStatus.Ok)
                        .Header("Content-Type", "text/event-stream; charset=utf-8")
                        .Header("X-Accel-Buffering", "no"))
                    .Content(new EventStreamContent(_events, _stopping)).Build();

            case "/api/logout" when method.Equals(RequestMethod.Post):
                _options.Sessions?.End(session);
                return Secured(request.Respond().Status(ResponseStatus.Ok)
                        .Header("Content-Type", "application/json; charset=utf-8")
                        .Header("Set-Cookie", ExpiredSessionCookie(secure)))
                    .Content(new BytesContent("{\"ok\":true}"u8.ToArray())).Build();

            default:
                return (_options.Library is { } library ? await library.HandleAsync(request, path) : null)
                       ?? Error(request, ResponseStatus.NotFound, "Not found");
        }
    }

    private async Task<IResponse> SignInAsync(IRequest request, AccessSettings access, string address, bool secure)
    {
        var throttle = _options.Throttle;
        if (throttle?.RetryAfter(address) is { } wait && wait > TimeSpan.Zero)
        {
            var seconds = (int)Math.Ceiling(wait.TotalSeconds);
            return Secured(request.Respond().Status(ResponseStatus.TooManyRequests)
                    .Header("Content-Type", "application/json; charset=utf-8")
                    .Header("Retry-After", seconds.ToString(CultureInfo.InvariantCulture)))
                .Content(new BytesContent(JsonSerializer.SerializeToUtf8Bytes(
                    new { error = "Too many attempts", code = "tooManyAttempts", retryAfterSeconds = seconds }, WebJson.Options)))
                .Build();
        }

        if (access.PasswordHash is not { } hash || _options.Sessions is not { } sessions)
        {
            return Json(request, ResponseStatus.Forbidden, new { error = "No password is set", code = "noPassword" });
        }

        var password = await ReadPasswordAsync(request);
        if (password is null)
        {
            return Json(request, ResponseStatus.BadRequest, new { error = "Bad request", code = "badRequest" });
        }

        // PBKDF2 runs for both right and wrong passwords, so the answer takes the same time.
        if (!await Task.Run(() => PasswordHasher.Verify(password, hash)))
        {
            _log.Warn($"Web interface: wrong password from {address}.");
            if (throttle?.RecordFailure(address) == true)
            {
                _log.Warn("Web interface: too many wrong passwords; sign-in is paused.");
                _options.SignInPaused?.Invoke();
            }

            return Json(request, ResponseStatus.Unauthorized, new { error = "Wrong password", code = "wrongPassword" });
        }

        throttle?.RecordSuccess(address);
        var value = sessions.Create(request.Header.Headers.GetEntry("User-Agent")?.ToString(), address);
        _log.Info($"Web interface: a device signed in from {address}.");
        var cookie = $"{SessionCookie}={value}; HttpOnly; SameSite=Strict; Path=/; Max-Age=" +
                     ((int)SessionStore.Lifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture) + (secure ? "; Secure" : "");
        return Secured(request.Respond().Status(ResponseStatus.Ok)
                .Header("Content-Type", "application/json; charset=utf-8")
                .Header("Set-Cookie", cookie))
            .Content(new BytesContent("{\"ok\":true}"u8.ToArray())).Build();
    }

    /// <summary>The request body, or null when there is none or it is larger than 16 KB (JSON bodies only).</summary>
    /// <param name="request">The request.</param>
    internal static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(IRequest request)
    {
        if (!request.HasBody)
        {
            return null;
        }

        await using var body = request.GetBody(HeaderAccess.Retain)?.AsStream();
        if (body is null)
        {
            return null;
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(length))) > 0)
        {
            length += read;
        }

        return length > MaxBodyBytes ? null : buffer.AsMemory(0, length);
    }

    private static async Task<string?> ReadPasswordAsync(IRequest request)
    {
        if (await ReadBodyAsync(request) is not { } body)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                   json.RootElement.TryGetProperty("password", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<IResponse> DeniedAsync(IRequest request, AccessDecision decision, bool api)
    {
        switch (decision)
        {
            case AccessDecision.Forbidden:
                return Error(request, ResponseStatus.Forbidden, "Host not allowed");
            case AccessDecision.SignIn when api:
                return Json(request, ResponseStatus.Unauthorized, new { error = "Sign in first", code = "signIn" });
            case AccessDecision.SignIn:
                return Redirect(request, "/login");
            case AccessDecision.OpenFromApp when api:
                return Json(request, ResponseStatus.Unauthorized, new { error = "Open the web interface from Magpienest", code = "openFromApp" });
        }

        var strings = await _backend.GetStringsAsync();
        var state = await _backend.GetStateAsync();
        string Text(string key) => WebUtility.HtmlEncode(strings.TryGetValue(key, out var s) ? s : key);
        var html = $"<!doctype html><html lang=\"{WebUtility.HtmlEncode(state.Language)}\"><head><meta charset=\"utf-8\">" +
                   "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                   $"<title>{Text("WebUi.Web.AppName")}</title><link rel=\"stylesheet\" href=\"/app.css\"></head>" +
                   $"<body class=\"message-page\"><main><h1>{Text("WebUi.Web.AppName")}</h1>" +
                   $"<p>{Text("WebUi.Web.OpenFromApp")}</p></main></body></html>";
        return Secured(request.Respond().Status(ResponseStatus.Unauthorized).Header("Content-Type", "text/html; charset=utf-8"))
            .Content(new BytesContent(Encoding.UTF8.GetBytes(html))).Build();
    }

    private IResponse File(IRequest request, string path) =>
        _files.Resolve(path) is { } file
            ? Secured(request.Respond().Status(ResponseStatus.Ok).Header("Content-Type", file.ContentType))
                .Content(new FileContent(file.Path, 0, new FileInfo(file.Path).Length)).Build()
            : Error(request, ResponseStatus.NotFound, "Not found");

    private static IResponse Redirect(IRequest request, string location) =>
        Secured(request.Respond().Status(ResponseStatus.Found).Header("Location", location)).Content(new BytesContent([])).Build();

    private static string ExpiredSessionCookie(bool secure) =>
        $"{SessionCookie}=; HttpOnly; SameSite=Strict; Path=/; Max-Age=0" + (secure ? "; Secure" : "");

    /// <summary>A JSON answer with the security headers.</summary>
    internal static IResponse Json(IRequest request, ResponseStatus status, object value) =>
        Secured(request.Respond().Status(status).Header("Content-Type", "application/json; charset=utf-8"))
            .Content(new BytesContent(JsonSerializer.SerializeToUtf8Bytes(value, WebJson.Options))).Build();

    /// <summary>A JSON error <c>{ error }</c>.</summary>
    internal static IResponse Error(IRequest request, ResponseStatus status, string message) =>
        Json(request, status, new { error = message });

    /// <summary>Headers every response carries.</summary>
    internal static IResponseBuilder Secured(IResponseBuilder builder, string cacheControl = "no-store") => builder
        .Header("Cache-Control", cacheControl)
        .Header("X-Content-Type-Options", "nosniff")
        .Header("Referrer-Policy", "no-referrer")
        .Header("X-Frame-Options", "DENY")
        .Header("Content-Security-Policy", ContentSecurityPolicy);

    private static string PathOf(IRequest request)
    {
        var path = request.Header.Path.ToString();
        var query = path.IndexOf('?');
        return query >= 0 ? path[..query] : path;
    }
}
