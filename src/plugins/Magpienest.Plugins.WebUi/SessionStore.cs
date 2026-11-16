using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Magpienest.Plugins.WebUi;

/// <summary>A signed-in browser, as settings lists it. The secret itself is never stored.</summary>
/// <param name="Id">Short public id (start of the hash) for "Sign out".</param>
/// <param name="Device">Browser and system, from the User-Agent.</param>
/// <param name="FirstSeen">When it signed in.</param>
/// <param name="LastSeen">Last request.</param>
/// <param name="LastAddress">Address of the last request.</param>
internal sealed record WebSession(string Id, string Device, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string LastAddress);

/// <summary>
/// Password sessions (phase 7 "Password and sessions"): random cookie values whose SHA-256 is kept in
/// <c>sessions.json</c>, sliding expiry of 30 days. Thread-safe.
/// </summary>
internal sealed class SessionStore
{
    /// <summary>Sessions end after this long without a request.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>"Last seen" is written to disk at most this often per session.</summary>
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Entry> _entries;

    /// <summary>Loads the sessions from a file (missing or damaged → none).</summary>
    /// <param name="path">The JSON file.</param>
    /// <param name="clock">Time source.</param>
    public SessionStore(string path, TimeProvider clock)
    {
        _path = path;
        _clock = clock;
        _entries = Load(path);
    }

    /// <summary>Raised after a session was added or removed (any thread).</summary>
    public event Action? Changed;

    /// <summary>The current sessions, newest activity first.</summary>
    public IReadOnlyList<WebSession> List()
    {
        lock (_gate)
        {
            Prune();
            return [.. _entries.Values.OrderByDescending(e => e.LastSeen).Select(e => e.ToSession())];
        }
    }

    /// <summary>Starts a session and returns the cookie value.</summary>
    /// <param name="userAgent">The browser's User-Agent.</param>
    /// <param name="address">Remote address.</param>
    public string Create(string? userAgent, string address)
    {
        var token = RequestGuards.NewToken();
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            Prune();
            _entries[HashOf(token)] = new Entry
            {
                Hash = HashOf(token),
                Device = DeviceLabel.From(userAgent),
                FirstSeen = now,
                LastSeen = now,
                LastAddress = address,
            };
            Save();
        }

        Changed?.Invoke();
        return token;
    }

    /// <summary>Whether a cookie value is a live session; a hit slides its expiry.</summary>
    /// <param name="token">The cookie value (may be null).</param>
    /// <param name="address">Remote address of this request.</param>
    public bool Validate(string? token, string address)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var hash = HashOf(token);
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!_entries.TryGetValue(hash, out var entry))
            {
                return false;
            }

            if (now - entry.LastSeen > Lifetime)
            {
                _entries.Remove(hash);
                Save();
                return false;
            }

            var save = now - entry.LastSeen > SaveInterval || entry.LastAddress != address;
            entry.LastSeen = now;
            entry.LastAddress = address;
            if (save)
            {
                Save();
            }

            return true;
        }
    }

    /// <summary>Ends the session with this cookie value (sign out from the browser).</summary>
    /// <param name="token">Cookie value.</param>
    public void End(string? token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            Remove(HashOf(token));
        }
    }

    /// <summary>Ends the session with this public id (sign out from settings).</summary>
    /// <param name="id">The id from <see cref="List"/>.</param>
    public void Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            var key = _entries.Keys.FirstOrDefault(k => k == id || k.StartsWith(id, StringComparison.Ordinal) && id.Length >= IdLength);
            removed = key is not null && _entries.Remove(key);
            if (removed)
            {
                Save();
            }
        }

        if (removed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Ends every session (sign out all devices; a password change does this too).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Save();
        }

        Changed?.Invoke();
    }

    private const int IdLength = 12;

    private static string HashOf(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private void Prune()
    {
        var now = _clock.GetUtcNow();
        var expired = _entries.Where(p => now - p.Value.LastSeen > Lifetime).Select(p => p.Key).ToList();
        foreach (var key in expired)
        {
            _entries.Remove(key);
        }

        if (expired.Count > 0)
        {
            Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries.Values.ToList(), WebJson.Options));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sessions still work in memory; they are lost at the next start.
        }
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), WebJson.Options) is { } list)
            {
                return list.Where(e => e.Hash.Length == 64).GroupBy(e => e.Hash).ToDictionary(g => g.Key, g => g.First());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged file signs everybody out; that is safe.
        }

        return [];
    }

    private sealed class Entry
    {
        public string Hash { get; set; } = "";

        public string Device { get; set; } = "";

        public DateTimeOffset FirstSeen { get; set; }

        public DateTimeOffset LastSeen { get; set; }

        public string LastAddress { get; set; } = "";

        public WebSession ToSession() => new(Hash[..IdLength], Device, FirstSeen, LastSeen, LastAddress);
    }
}

/// <summary>A short "browser on system" label from a User-Agent.</summary>
internal static class DeviceLabel
{
    /// <summary>For example "Edge · Windows", "Safari · iPhone"; "?" parts when unknown.</summary>
    /// <param name="userAgent">User-Agent header.</param>
    public static string From(string? userAgent)
    {
        var ua = userAgent ?? "";
        string browser = ua switch
        {
            _ when ua.Contains("Edg/", StringComparison.Ordinal) => "Edge",
            _ when ua.Contains("OPR/", StringComparison.Ordinal) => "Opera",
            _ when ua.Contains("SamsungBrowser", StringComparison.Ordinal) => "Samsung Internet",
            _ when ua.Contains("Firefox/", StringComparison.Ordinal) || ua.Contains("FxiOS", StringComparison.Ordinal) => "Firefox",
            _ when ua.Contains("Chrome/", StringComparison.Ordinal) || ua.Contains("CriOS", StringComparison.Ordinal) => "Chrome",
            _ when ua.Contains("Safari/", StringComparison.Ordinal) => "Safari",
            _ => "?",
        };
        string system = ua switch
        {
            _ when ua.Contains("iPhone", StringComparison.Ordinal) => "iPhone",
            _ when ua.Contains("iPad", StringComparison.Ordinal) => "iPad",
            _ when ua.Contains("Android", StringComparison.Ordinal) => "Android",
            _ when ua.Contains("Windows", StringComparison.Ordinal) => "Windows",
            _ when ua.Contains("Mac OS X", StringComparison.Ordinal) => "macOS",
            _ when ua.Contains("Linux", StringComparison.Ordinal) => "Linux",
            _ => "?",
        };
        return browser + " · " + system;
    }
}
