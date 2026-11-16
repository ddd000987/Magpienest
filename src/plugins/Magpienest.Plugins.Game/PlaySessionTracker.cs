using System.Diagnostics;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.Game;

/// <summary>What the tracker needs to know about the machine's processes; replaced by a fake in tests.</summary>
internal interface IProcessProbe
{
    /// <summary>(pid, full exe path) of every process in the current session that can be inspected.</summary>
    IReadOnlyList<(int Pid, string Path)> Snapshot();

    /// <summary>Whether the process with this id is still alive.</summary>
    /// <param name="pid">Process id.</param>
    bool IsRunning(int pid);
}

/// <summary>A clock that only moves forward; replaced by a fake in tests.</summary>
internal interface IMonotonicClock
{
    /// <summary>Time since an arbitrary fixed point.</summary>
    TimeSpan Now { get; }
}

/// <summary>The real clock, based on <see cref="Stopwatch"/>.</summary>
internal sealed class StopwatchClock : IMonotonicClock
{
    /// <inheritdoc />
    public TimeSpan Now => Stopwatch.GetElapsedTime(0);
}

/// <summary>
/// Follows running games. Pure logic: the caller drives it by calling <see cref="PollAsync"/> every
/// <see cref="PollInterval"/>; processes come from an <see cref="IProcessProbe"/>, time from an
/// <see cref="IMonotonicClock"/> and the stored play time is written through a callback.
/// Thread-safe; process inspection and persistence never run while the internal lock is held.
/// </summary>
/// <param name="probe">Process source.</param>
/// <param name="clock">Monotonic clock.</param>
/// <param name="persist">Adds whole seconds of play time to a game (item id, seconds); may be called with 0.</param>
/// <param name="ownExePath">Magpienest's own executable; a game folder that contains it is tracked by process id only.</param>
/// <param name="log">Where persistence failures go.</param>
internal sealed class PlaySessionTracker(
    IProcessProbe probe,
    IMonotonicClock clock,
    Func<long, long, Task> persist,
    string? ownExePath,
    ILog? log = null)
{
    /// <summary>How often the caller polls.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>A session ends after this many consecutive polls without the game, once it was seen running.</summary>
    public const int MissedPollsToEnd = 3;

    /// <summary>A session that never sees its game ends after this long.</summary>
    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);

    /// <summary>A gap between two polls longer than this (sleep, hibernation) is not counted.</summary>
    public static readonly TimeSpan MaxCountedGap = TimeSpan.FromSeconds(30);

    /// <summary>Play time is written to the library this often while a game runs.</summary>
    public static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<long, Session> _sessions = [];
    private readonly string? _ownExe = NormalizePath(ownExePath);

    /// <summary>Raised (on any thread) after a session started or ended.</summary>
    public event Action? SessionsChanged;

    /// <summary>Whether any game is being tracked.</summary>
    public bool HasSessions
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count > 0;
            }
        }
    }

    /// <summary>Starts tracking a game just launched. Returns false when it is already tracked.</summary>
    /// <param name="item">The game.</param>
    /// <param name="pid">Id of the process that was started, or null when it is unknown (then only the folder counts).</param>
    public bool Start(LibraryItem item, int? pid) => Start(item.Id, GameExtra.EffectiveInstallDir(item), pid);

    /// <summary>Starts tracking a game just launched. Returns false when it is already tracked.</summary>
    /// <param name="itemId">Library id of the game.</param>
    /// <param name="installDir">The game's folder.</param>
    /// <param name="pid">Id of the process that was started, or null when it is unknown (then only the folder counts).</param>
    public bool Start(long itemId, string installDir, int? pid)
    {
        var prefix = DirectoryPrefix(installDir, _ownExe);
        lock (_gate)
        {
            if (_sessions.ContainsKey(itemId))
            {
                return false;
            }

            var now = clock.Now;
            _sessions[itemId] = new Session(itemId, pid, prefix, now);
        }

        SessionsChanged?.Invoke();
        return true;
    }

    /// <summary>Whether the game has a session.</summary>
    /// <param name="itemId">Library id.</param>
    public bool IsTracking(long itemId)
    {
        lock (_gate)
        {
            return _sessions.ContainsKey(itemId);
        }
    }

    /// <summary>Play time of the current session (including the time since the last poll), or null when there is none.</summary>
    /// <param name="itemId">Library id.</param>
    public TimeSpan? SessionTime(long itemId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(itemId, out var session))
            {
                return null;
            }

            var extra = session.LastRunning ? Min(clock.Now - session.LastPoll, PollInterval) : TimeSpan.Zero;
            return session.Total + (extra > TimeSpan.Zero ? extra : TimeSpan.Zero);
        }
    }

    /// <summary>Ends the session now (the user asked to stop tracking) and stores the unsaved time.</summary>
    /// <param name="itemId">Library id.</param>
    public async Task StopAsync(long itemId)
    {
        Session? session;
        long seconds;
        lock (_gate)
        {
            if (!_sessions.Remove(itemId, out session))
            {
                return;
            }

            seconds = Unsaved(session);
        }

        await SaveAsync(session, seconds, ended: true).ConfigureAwait(false);
        SessionsChanged?.Invoke();
    }

    /// <summary>Stores the unsaved whole seconds of every session (shutdown). Sessions stay.</summary>
    public async Task FlushAllAsync()
    {
        List<(Session Session, long Seconds)> work = [];
        lock (_gate)
        {
            foreach (var session in _sessions.Values)
            {
                if (Unsaved(session) is > 0 and var seconds)
                {
                    session.Saved += seconds;
                    work.Add((session, seconds));
                }
            }
        }

        foreach (var (session, seconds) in work)
        {
            await SaveAsync(session, seconds, ended: false).ConfigureAwait(false);
        }
    }

    /// <summary>One tick: looks at the processes, adds play time, ends finished sessions, saves what is due.</summary>
    public async Task PollAsync()
    {
        // 1. What to look for (under the lock), 2. look (no lock: listing processes can take a while), 3. apply.
        List<Session> sessions;
        lock (_gate)
        {
            if (_sessions.Count == 0)
            {
                return;
            }

            sessions = [.. _sessions.Values];
        }

        var verdicts = new List<(Session Session, bool Running)>(sessions.Count);
        IReadOnlyList<(int Pid, string Path)>? snapshot = null;
        foreach (var session in sessions)
        {
            var running = session.Pid is { } pid && probe.IsRunning(pid);
            if (!running && session.DirectoryPrefix is not null)
            {
                snapshot ??= probe.Snapshot();
                running = snapshot.Any(p => IsUnder(session.DirectoryPrefix, p.Path));
            }

            verdicts.Add((session, running));
        }

        var saves = new List<(Session Session, long Seconds, bool Ended)>();
        lock (_gate)
        {
            var now = clock.Now;
            foreach (var (session, running) in verdicts)
            {
                if (!_sessions.TryGetValue(session.ItemId, out var current) || !ReferenceEquals(current, session))
                {
                    continue; // stopped while we were looking
                }

                var gap = now - session.LastPoll;
                session.LastPoll = now;
                session.LastRunning = running;
                if (running)
                {
                    session.SeenRunning = true;
                    session.MissedPolls = 0;
                    if (gap <= MaxCountedGap)
                    {
                        session.Total += gap;
                    }
                }
                else
                {
                    session.MissedPolls++;
                }

                var ended = !running && (session.SeenRunning
                    ? session.MissedPolls >= MissedPollsToEnd
                    : now - session.Started >= StartupGrace);
                if (ended)
                {
                    _sessions.Remove(session.ItemId);
                    saves.Add((session, Unsaved(session), true));
                }
                else if (now - session.LastSave >= PersistInterval && Unsaved(session) is > 0 and var seconds)
                {
                    session.Saved += seconds;
                    session.LastSave = now;
                    saves.Add((session, seconds, false));
                }
            }
        }

        foreach (var (session, seconds, ended) in saves)
        {
            await SaveAsync(session, seconds, ended).ConfigureAwait(false);
        }

        if (saves.Any(s => s.Ended))
        {
            SessionsChanged?.Invoke();
        }
    }

    /// <summary>
    /// The folder to look for, with a trailing separator; null when folder matching must stay off: the folder is
    /// a drive root, is shorter than 4 characters, cannot be read, or contains Magpienest's own program.
    /// </summary>
    /// <param name="installDir">The game's folder.</param>
    /// <param name="ownExe">Magpienest's own program (normalized), or null.</param>
    internal static string? DirectoryPrefix(string? installDir, string? ownExe)
    {
        // "C:" would otherwise be read as the current folder of that drive.
        if (string.IsNullOrWhiteSpace(installDir) || Path.TrimEndingDirectorySeparator(installDir.Trim()).Length < 4)
        {
            return null;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }

        if (full.Length < 4)
        {
            return null;
        }

        var root = Path.GetPathRoot(full);
        if (root is not null && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var prefix = full + Path.DirectorySeparatorChar;
        return ownExe is not null && IsUnder(prefix, ownExe) ? null : prefix;
    }

    /// <summary>Whether a program path lies inside the folder. The folder must end with a separator (boundary-safe).</summary>
    /// <param name="directoryPrefix">Folder with a trailing separator.</param>
    /// <param name="processPath">Full path of a program.</param>
    internal static bool IsUnder(string directoryPrefix, string processPath)
    {
        var path = NormalizePath(processPath);
        return path is not null && path.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>Whole seconds played that were not written yet. Call under the lock.</summary>
    private static long Unsaved(Session session) => session.Total.Ticks / TimeSpan.TicksPerSecond - session.Saved;

    /// <summary>
    /// Writes <paramref name="seconds"/>. An ended session is written even with 0 seconds: the update makes the
    /// library announce a change, which is how the tile stops saying "Running".
    /// </summary>
    private async Task SaveAsync(Session session, long seconds, bool ended)
    {
        if (seconds <= 0 && !ended)
        {
            return;
        }

        try
        {
            await persist(session.ItemId, Math.Max(0, seconds)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.Warn($"Saving the play time of item {session.ItemId} failed.", ex);
            if (!ended)
            {
                // Try again at the next save.
                lock (_gate)
                {
                    session.Saved -= seconds;
                }
            }
        }
    }

    private sealed class Session(long itemId, int? pid, string? directoryPrefix, TimeSpan started)
    {
        public long ItemId { get; } = itemId;

        public int? Pid { get; } = pid;

        /// <summary>Null: only the process id counts.</summary>
        public string? DirectoryPrefix { get; } = directoryPrefix;

        public TimeSpan Started { get; } = started;

        public TimeSpan LastPoll { get; set; } = started;

        public TimeSpan LastSave { get; set; } = started;

        public TimeSpan Total { get; set; }

        /// <summary>Whole seconds already handed to the persistence callback.</summary>
        public long Saved { get; set; }

        public bool SeenRunning { get; set; }

        public bool LastRunning { get; set; }

        public int MissedPolls { get; set; }
    }
}
