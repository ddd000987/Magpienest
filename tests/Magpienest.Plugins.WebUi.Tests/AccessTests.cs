using System.Net;
using Magpienest.Plugins.WebUi;

namespace Magpienest.Plugins.WebUi.Tests;

/// <summary>The pure parts of remote access: access table, address classes, password hashes, throttle, sessions.</summary>
public sealed class AccessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mn-webui-access-" + Guid.NewGuid().ToString("N"));

    public AccessTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- AccessPolicy: every combination against the rules written out once more ----

    public static TheoryData<int, int, bool, bool, bool, bool, bool> AllCombinations()
    {
        var data = new TheoryData<int, int, bool, bool, bool, bool, bool>();
        bool[] flags = [false, true];
        foreach (var mode in Enum.GetValues<AccessMode>())
        foreach (var from in Enum.GetValues<AddressClass>())
        foreach (var host in flags)
        foreach (var token in flags)
        foreach (var session in flags)
        foreach (var password in flags)
        foreach (var requireAtHome in flags)
        {
            data.Add((int)mode, (int)from, host, token, session, password, requireAtHome);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void Access_table(int modeValue, int fromValue, bool host, bool token, bool session, bool password, bool requireAtHome)
    {
        var mode = (AccessMode)modeValue;
        var from = (AddressClass)fromValue;
        var actual = AccessPolicy.Decide(new AccessRequest(mode, from, host, token, session, password, requireAtHome));

        AccessDecision expected;
        if ((from == AddressClass.Internet && mode != AccessMode.Internet) || (from == AddressClass.Home && mode == AccessMode.Local))
        {
            expected = AccessDecision.Drop;
        }
        else if (session)
        {
            expected = AccessDecision.Allow;
        }
        else if (from == AddressClass.Loopback)
        {
            expected = token ? (host ? AccessDecision.Allow : AccessDecision.Forbidden)
                             : password ? AccessDecision.SignIn : AccessDecision.OpenFromApp;
        }
        else if (from == AddressClass.Home && !requireAtHome)
        {
            expected = host ? AccessDecision.Allow : AccessDecision.Forbidden;
        }
        else
        {
            expected = AccessDecision.SignIn;
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Token_never_opens_the_door_from_another_device()
    {
        foreach (var from in new[] { AddressClass.Home, AddressClass.Internet })
        {
            var decision = AccessPolicy.Decide(new AccessRequest(AccessMode.Internet, from, true, HasToken: true, false, true, true));
            Assert.Equal(AccessDecision.SignIn, decision);
        }
    }

    // ---- Address classes ----

    [Theory]
    [InlineData("127.0.0.1", true, "Loopback")]
    [InlineData("::1", true, "Loopback")]
    [InlineData("::ffff:127.0.0.1", true, "Loopback")]
    [InlineData("10.1.2.3", true, "Home")]
    [InlineData("172.16.0.1", true, "Home")]
    [InlineData("172.31.255.255", true, "Home")]
    [InlineData("172.32.0.1", true, "Internet")]
    [InlineData("192.168.1.242", true, "Home")]
    [InlineData("::ffff:192.168.1.5", true, "Home")]
    [InlineData("169.254.10.10", true, "Home")]
    [InlineData("100.64.0.1", true, "Home")]
    [InlineData("100.127.255.254", true, "Home")]
    [InlineData("100.64.0.1", false, "Internet")]
    [InlineData("100.128.0.1", true, "Internet")]
    [InlineData("26.236.160.167", true, "Internet")]
    [InlineData("8.8.8.8", true, "Internet")]
    [InlineData("fd12:3456::1", true, "Home")]
    [InlineData("fe80::1", true, "Home")]
    [InlineData("2001:4860:4860::8888", true, "Internet")]
    [InlineData("::ffff:8.8.8.8", true, "Internet")]
    public void Address_classes(string address, bool tailscale, string expected) =>
        Assert.Equal(Enum.Parse<AddressClass>(expected), AddressClassifier.Classify(IPAddress.Parse(address), tailscale));

    // ---- Password hashes ----

    [Fact]
    public void Password_hash_round_trip()
    {
        var hash = PasswordHasher.Hash("correct horse", iterations: 1000);
        Assert.StartsWith("pbkdf2-sha256$1000$", hash);
        Assert.True(PasswordHasher.Verify("correct horse", hash));
        Assert.False(PasswordHasher.Verify("correct hors", hash));
        Assert.False(PasswordHasher.Verify("Correct horse", hash));
        Assert.False(PasswordHasher.Verify(null, hash));
        Assert.NotEqual(hash, PasswordHasher.Hash("correct horse", iterations: 1000));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("pbkdf2-sha256$x$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$1000$!!!$AAAA")]
    [InlineData("md5$1000$AAAA$AAAA")]
    public void Malformed_hashes_never_match(string? stored) => Assert.False(PasswordHasher.Verify("anything", stored));

    [Fact]
    public void Default_hash_uses_600000_iterations() =>
        Assert.StartsWith("pbkdf2-sha256$600000$", PasswordHasher.Hash("12345678"));

    [Theory]
    [InlineData("1234567", false)]
    [InlineData("12345678", true)]
    [InlineData(null, false)]
    public void Minimum_length(string? password, bool ok) => Assert.Equal(ok, PasswordHasher.IsAcceptable(password));

    // ---- Throttle ----

    [Fact]
    public void Five_failures_block_that_address_for_15_minutes()
    {
        var clock = new FakeClock();
        var throttle = new LoginThrottle(clock);
        for (var i = 0; i < 4; i++)
        {
            Assert.False(throttle.RecordFailure("1.1.1.1"));
            Assert.Equal(TimeSpan.Zero, throttle.RetryAfter("1.1.1.1"));
        }

        throttle.RecordFailure("1.1.1.1");
        Assert.Equal(TimeSpan.FromMinutes(15), throttle.RetryAfter("1.1.1.1"));
        Assert.Equal(TimeSpan.Zero, throttle.RetryAfter("2.2.2.2"));

        clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(TimeSpan.FromMinutes(1), throttle.RetryAfter("1.1.1.1"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.Zero, throttle.RetryAfter("1.1.1.1"));
    }

    [Fact]
    public void Failures_older_than_the_window_do_not_count_and_success_clears()
    {
        var clock = new FakeClock();
        var throttle = new LoginThrottle(clock);
        for (var i = 0; i < 4; i++)
        {
            throttle.RecordFailure("1.1.1.1");
        }

        clock.Advance(TimeSpan.FromMinutes(16));
        throttle.RecordFailure("1.1.1.1");
        Assert.Equal(TimeSpan.Zero, throttle.RetryAfter("1.1.1.1"));

        for (var i = 0; i < 3; i++)
        {
            throttle.RecordFailure("1.1.1.1");
        }

        throttle.RecordSuccess("1.1.1.1");
        throttle.RecordFailure("1.1.1.1");
        Assert.Equal(TimeSpan.Zero, throttle.RetryAfter("1.1.1.1"));
    }

    [Fact]
    public void Twenty_failures_from_anywhere_pause_every_sign_in()
    {
        var clock = new FakeClock();
        var throttle = new LoginThrottle(clock);
        var started = 0;
        for (var i = 0; i < LoginThrottle.GlobalLimit; i++)
        {
            // Different addresses, so no single address is blocked.
            if (throttle.RecordFailure($"10.0.0.{i}"))
            {
                started++;
            }
        }

        Assert.Equal(1, started);
        Assert.Equal(TimeSpan.FromMinutes(15), throttle.RetryAfter("192.168.1.1"));
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(TimeSpan.Zero, throttle.RetryAfter("192.168.1.1"));
    }

    // ---- Sessions ----

    [Fact]
    public void Session_slides_and_expires_after_30_idle_days()
    {
        var clock = new FakeClock();
        var store = new SessionStore(Path.Combine(_dir, "sessions.json"), clock);
        var token = store.Create("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) Safari/604.1", "192.168.1.5");
        Assert.True(store.Validate(token, "192.168.1.5"));
        Assert.False(store.Validate(token + "x", "192.168.1.5"));
        Assert.False(store.Validate(null, "192.168.1.5"));

        clock.Advance(TimeSpan.FromDays(29));
        Assert.True(store.Validate(token, "192.168.1.6"));
        clock.Advance(TimeSpan.FromDays(29));
        Assert.True(store.Validate(token, "192.168.1.6"));
        clock.Advance(TimeSpan.FromDays(31));
        Assert.False(store.Validate(token, "192.168.1.6"));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Sessions_survive_a_restart_but_only_as_hashes()
    {
        var clock = new FakeClock();
        var path = Path.Combine(_dir, "sessions.json");
        var token = new SessionStore(path, clock).Create("Mozilla/5.0 (Windows NT 10.0) Chrome/150.0 Edg/150.0", "10.0.0.2");
        Assert.DoesNotContain(token, File.ReadAllText(path));

        var reloaded = new SessionStore(path, clock);
        Assert.True(reloaded.Validate(token, "10.0.0.2"));
        var session = Assert.Single(reloaded.List());
        Assert.Equal("Edge · Windows", session.Device);
        Assert.Equal("10.0.0.2", session.LastAddress);
    }

    [Fact]
    public void Sign_out_one_all_and_from_the_browser()
    {
        var clock = new FakeClock();
        var store = new SessionStore(Path.Combine(_dir, "sessions.json"), clock);
        var changes = 0;
        store.Changed += () => changes++;
        var a = store.Create("Firefox/140.0 (Android)", "192.168.1.2");
        var b = store.Create("Safari/604.1 (iPad)", "192.168.1.3");
        var c = store.Create(null, "192.168.1.4");
        Assert.Equal(3, store.List().Count);

        var idOfB = store.List().Single(s => s.LastAddress == "192.168.1.3").Id;
        store.Remove(idOfB);
        Assert.False(store.Validate(b, "x"));
        Assert.True(store.Validate(a, "x"));

        store.End(a);
        Assert.False(store.Validate(a, "x"));
        Assert.True(store.Validate(c, "x"));

        store.Clear();
        Assert.False(store.Validate(c, "x"));
        Assert.Equal(6, changes);
    }

    [Fact]
    public void A_damaged_session_file_signs_everybody_out()
    {
        var path = Path.Combine(_dir, "sessions.json");
        File.WriteAllText(path, "{ not json");
        Assert.Empty(new SessionStore(path, new FakeClock()).List());
    }

    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1", "Safari · iPhone")]
    [InlineData("Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 Chrome/150.0 Mobile Safari/537.36", "Chrome · Android")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:140.0) Gecko/20100101 Firefox/140.0", "Firefox · Windows")]
    [InlineData(null, "? · ?")]
    public void Device_labels(string? userAgent, string expected) => Assert.Equal(expected, DeviceLabel.From(userAgent));

    [Fact]
    public void Extra_host_parsing()
    {
        Assert.Equal(["nas.example.com", "pc-2"], WebUiSettings.ParseHosts(" NAS.example.com., pc-2 ; nas.example.com bad/name "));
        Assert.Empty(WebUiSettings.ParseHosts(null));
    }

    [Fact]
    public void Internet_mode_forces_https()
    {
        var settings = new WebUiSettings(new MemoryConfig());
        Assert.Equal(HttpsMode.Off, settings.EffectiveHttps);
        settings.Access = AccessMode.Home;
        Assert.Equal(HttpsMode.Off, settings.EffectiveHttps);
        settings.Access = AccessMode.Internet;
        Assert.Equal(HttpsMode.Automatic, settings.EffectiveHttps);
        settings.Https = HttpsMode.Own;
        Assert.Equal(HttpsMode.Own, settings.EffectiveHttps);
    }

    [Fact]
    public void Self_signed_certificate_covers_the_names_and_is_a_server_certificate()
    {
        var pfx = CertificateStore.CreateSelfSigned(["localhost", "127.0.0.1", "192.168.1.242", "mypc"], "pw", DateTimeOffset.UtcNow);
        using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(pfx, "pw");
        Assert.True(certificate.HasPrivateKey);
        var san = certificate.Extensions["2.5.29.17"]!.Format(false);
        Assert.Contains("localhost", san);
        Assert.Contains("192.168.1.242", san);
        Assert.Contains("mypc", san);
        Assert.True(certificate.NotAfter > DateTime.Now.AddYears(4));
    }

    [Fact]
    public void Protected_secrets_round_trip_for_this_user()
    {
        var secret = CertificateStore.Protect("s3cret");
        Assert.NotEqual("s3cret", secret);
        Assert.Equal("s3cret", CertificateStore.Unprotect(secret));
        Assert.Null(CertificateStore.Unprotect("AAAA"));
        Assert.Null(CertificateStore.Unprotect(null));
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
