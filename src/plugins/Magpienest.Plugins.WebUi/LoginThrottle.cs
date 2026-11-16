namespace Magpienest.Plugins.WebUi;

/// <summary>
/// Brute-force limits for the password (phase 7 "Password and sessions"): 5 failures from one address within
/// 15 minutes refuse that address for 15 minutes; 20 failures in total pause every sign-in for 15 minutes. Thread-safe.
/// </summary>
internal sealed class LoginThrottle(TimeProvider clock)
{
    /// <summary>Failures from one address before it is refused.</summary>
    public const int PerAddressLimit = 5;

    /// <summary>Failures in total before all sign-ins pause.</summary>
    public const int GlobalLimit = 20;

    /// <summary>Window in which failures count, and how long a block lasts.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _byAddress = [];
    private readonly List<DateTimeOffset> _all = [];
    private readonly Dictionary<string, DateTimeOffset> _blockedUntil = [];
    private DateTimeOffset _pausedUntil;

    /// <summary>How long this address must wait before it may try again; zero when it may try now.</summary>
    /// <param name="address">Remote address.</param>
    public TimeSpan RetryAfter(string address)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            var wait = _pausedUntil - now;
            if (_blockedUntil.TryGetValue(address, out var until) && until - now > wait)
            {
                wait = until - now;
            }

            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
    }

    /// <summary>Counts a wrong password. Returns true when this failure started a global pause (tell the user).</summary>
    /// <param name="address">Remote address.</param>
    public bool RecordFailure(string address)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (!_byAddress.TryGetValue(address, out var list))
            {
                _byAddress[address] = list = [];
            }

            list.Add(now);
            list.RemoveAll(t => now - t > Window);
            if (list.Count >= PerAddressLimit)
            {
                _blockedUntil[address] = now + Window;
                list.Clear();
            }

            _all.Add(now);
            _all.RemoveAll(t => now - t > Window);
            if (_all.Count >= GlobalLimit && _pausedUntil <= now)
            {
                _pausedUntil = now + Window;
                _all.Clear();
                return true;
            }

            // Old addresses are forgotten so the tables cannot grow without bound.
            foreach (var key in _byAddress.Where(p => p.Value.All(t => now - t > Window)).Select(p => p.Key).ToList())
            {
                _byAddress.Remove(key);
            }

            foreach (var key in _blockedUntil.Where(p => p.Value <= now).Select(p => p.Key).ToList())
            {
                _blockedUntil.Remove(key);
            }

            return false;
        }
    }

    /// <summary>A correct password clears that address's failures.</summary>
    /// <param name="address">Remote address.</param>
    public void RecordSuccess(string address)
    {
        lock (_gate)
        {
            _byAddress.Remove(address);
        }
    }
}
