using Magpienest.Plugins.WebUi;

namespace Magpienest.Plugins.WebUi.Tests;

public sealed class RequestGuardsTests
{
    [Theory]
    [InlineData("127.0.0.1:47290", "127.0.0.1")]
    [InlineData("localhost", "localhost")]
    [InlineData("[::1]:8080", "::1")]
    [InlineData("[fe80::1]", "fe80::1")]
    [InlineData("example.com.", "example.com")]
    [InlineData("", null)]
    [InlineData("host:notaport", null)]
    [InlineData("[::1", null)]
    [InlineData("[::1]x", null)]
    public void HostName_strips_port_and_brackets(string header, string? expected) =>
        Assert.Equal(expected, RequestGuards.HostName(header));

    [Theory]
    [InlineData("127.0.0.1:47290", true)]
    [InlineData("localhost:47290", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("192.168.1.20:47290", true)]
    [InlineData("[::1]:47290", true)]
    [InlineData("MYPC:47290", true)]
    [InlineData("mypc.local:47290", true)]
    [InlineData("nas.example.org", true)]
    [InlineData("evil.example.com:47290", false)]
    [InlineData("127.0.0.1.evil.com", false)]
    [InlineData("1:47290", false)]
    [InlineData("localhost.evil.com", false)]
    [InlineData(null, false)]
    public void Only_local_names_and_ip_literals_are_allowed_hosts(string? header, bool allowed) =>
        Assert.Equal(allowed, RequestGuards.IsAllowedHost(header, "MyPC", ["nas.example.org"]));

    [Theory]
    [InlineData("http://127.0.0.1:47290", false, "127.0.0.1:47290", true)]
    [InlineData("https://127.0.0.1:47290", true, "127.0.0.1:47290", true)]
    [InlineData("http://127.0.0.1:47290", true, "127.0.0.1:47290", false)]
    [InlineData("http://localhost:47290", false, "127.0.0.1:47290", false)]
    [InlineData("https://evil.com", true, "127.0.0.1:47290", false)]
    [InlineData("http://example.org", false, "example.org:80", true)]
    [InlineData("null", false, "127.0.0.1:47290", false)]
    [InlineData(null, false, "127.0.0.1:47290", false)]
    public void Writes_need_a_same_origin_header(string? origin, bool secure, string host, bool expected) =>
        Assert.Equal(expected, RequestGuards.IsSameOrigin(origin, secure, host));

    [Fact]
    public void Cookie_and_bearer_are_read()
    {
        Assert.Equal("abc", RequestGuards.Cookie("a=1; mn_token=abc; b=2", "mn_token"));
        Assert.Null(RequestGuards.Cookie("xmn_token=abc", "mn_token"));
        Assert.Null(RequestGuards.Cookie(null, "mn_token"));
        Assert.Equal("t0k", RequestGuards.Bearer("Bearer t0k"));
        Assert.Null(RequestGuards.Bearer("Basic t0k"));
    }

    [Fact]
    public void Tokens_are_random_base64url_of_32_bytes()
    {
        var a = RequestGuards.NewToken();
        var b = RequestGuards.NewToken();
        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length);
        Assert.DoesNotContain(a, c => c is '+' or '/' or '=');
    }

    [Fact]
    public void Secret_comparison()
    {
        Assert.True(RequestGuards.SecretEquals("abc", "abc"));
        Assert.False(RequestGuards.SecretEquals("abd", "abc"));
        Assert.False(RequestGuards.SecretEquals("ab", "abc"));
        Assert.False(RequestGuards.SecretEquals(null, "abc"));
        Assert.False(RequestGuards.SecretEquals("", ""));
    }

    [Theory]
    [InlineData("47290", 47290)]
    [InlineData(" 1024 ", 1024)]
    [InlineData("65535", 65535)]
    [InlineData("1023", null)]
    [InlineData("65536", null)]
    [InlineData("-5", null)]
    [InlineData("80a", null)]
    [InlineData("", null)]
    public void Port_parsing(string text, int? expected) => Assert.Equal(expected, WebUiSettings.ParsePort(text));

    [Fact]
    public void Port_override_from_command_line()
    {
        Assert.Equal(5555, WebUiSettings.PortOverride(["--smoke-test", "--webui-port", "5555"]));
        Assert.Null(WebUiSettings.PortOverride(["--webui-port"]));
        Assert.Null(WebUiSettings.PortOverride(["--webui-port", "x"]));
        Assert.Null(WebUiSettings.PortOverride([]));
    }
}
