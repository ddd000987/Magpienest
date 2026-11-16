using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.Game.Tests;

public sealed class PlaySessionTrackerTests
{
    private const long Id = 7;
    private const string Own = @"C:\Apps\Magpienest\Magpienest.exe";

    // ----- simple cases -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ASimpleRunIsCountedAndEndsAfterThreeMissedPolls()
    {
        var h = new Harness();
        h.Probe.Alive.Add(100);
        h.Tracker.Start(Id, @"C:\Games\Foo", 100);
        Assert.True(h.Tracker.IsTracking(Id));

        await h.StepAsync(5);                    // running at 2, 4, 6, 8, 10 s
        Assert.Equal(TimeSpan.FromSeconds(10), h.Tracker.SessionTime(Id));

        h.Probe.Alive.Clear();
        await h.StepAsync(2);                    // two missed polls: still tracked
        Assert.True(h.Tracker.IsTracking(Id));
        await h.StepAsync(1);                    // the third ends it
        Assert.False(h.Tracker.IsTracking(Id));
        Assert.Null(h.Tracker.SessionTime(Id));
        Assert.Equal(10, h.TotalSaved(Id));
        Assert.Equal(2, h.Changes);              // started, ended
    }

    [Fact]
    public async Task ALauncherThatExitsAtOnceIsFollowedToTheChildWhichIsCountedOnlyFromWhenItAppears()
    {
        var h = new Harness();
        h.Tracker.Start(Id, @"C:\Games\Foo", 100); // pid 100 is already dead
        await h.StepAsync(4);                      // 2..8 s: nothing running
        Assert.True(h.Tracker.IsTracking(Id));
        Assert.Equal(TimeSpan.Zero, h.Tracker.SessionTime(Id));

        h.Probe.Processes.Add((200, @"C:\Games\Foo\bin\game.exe")); // appears 10 s after the start
        await h.StepAsync(11);                                       // running at 10, 12, ... 30 s
        Assert.True(h.Tracker.IsTracking(Id));
        // 11 polls of 2 s: the first one counts the poll interval that ended when the game was seen, not the 10 s before.
        Assert.Equal(TimeSpan.FromSeconds(22), h.Tracker.SessionTime(Id));

        h.Probe.Processes.Clear();
        await h.StepAsync(3);
        Assert.False(h.Tracker.IsTracking(Id));
        Assert.Equal(22, h.TotalSaved(Id));
    }

    [Fact]
    public async Task NothingEverAppearingEndsTheSessionAfterSixtySecondsWithNoPlayTime()
    {
        var h = new Harness();
        h.Tracker.Start(Id, @"C:\Games\Foo", 100);
        await h.StepAsync(29);                     // 58 s
        Assert.True(h.Tracker.IsTracking(Id));
        await h.StepAsync(1);                      // 60 s
        Assert.False(h.Tracker.IsTracking(Id));
        Assert.Equal([(Id, 0L)], h.Saves);         // the update with 0 s is what refreshes the tile
    }

    [Fact]
    public async Task ShortGapsDoNotEndTheSessionAndAddNoTime()
    {
        var h = new Harness();
        h.Probe.Alive.Add(100);
        h.Tracker.Start(Id, @"C:\Games\Foo", 100);
        await h.StepAsync(2);                      // running at 2, 4          -> 4 s
        h.Probe.Alive.Clear();
        await h.StepAsync(2);                      // missed at 6, 8           -> still 4 s
        Assert.True(h.Tracker.IsTracking(Id));
        Assert.Equal(TimeSpan.FromSeconds(4), h.Tracker.SessionTime(Id));

        h.Probe.Alive.Add(100);
        await h.StepAsync(1);                      // running again at 10      -> +2 s
        h.Probe.Alive.Clear();
        await h.StepAsync(2);                      // missed at 12, 14 (the counter started again)
        Assert.True(h.Tracker.IsTracking(Id));
        h.Probe.Alive.Add(100);
        await h.StepAsync(1);                      // running at 16            -> +2 s
        Assert.True(h.Tracker.IsTracking(Id));
        Assert.Equal(TimeSpan.FromSeconds(8), h.Tracker.SessionTime(Id));
    }

    [Fact]
    public async Task AGapLongerThanThirtySecondsIsNotCounted()
    {
        var h = new Harness();
        h.Probe.Alive.Add(100);
        h.Tracker.Start(Id, @"C:\Games\Foo", 100);
        await h.StepAsync(2);                      // 4 s
        h.Clock.Now += TimeSpan.FromHours(1);      // sleep
        await h.StepAsync(1);                      // still running, but the hour is not counted (and neither is the 2 s)
        Assert.Equal(TimeSpan.FromSeconds(4), h.Tracker.SessionTime(Id));
        await h.StepAsync(1);
        Assert.Equal(TimeSpan.FromSeconds(6), h.Tracker.SessionTime(Id));
        await h.Tracker.StopAsync(Id);
        Assert.Equal(6, h.TotalSaved(Id));
    }

    [Fact]
    public async Task AGapOfExactlyThirtySecondsIsStillCounted()
    {
        var h = new Harness();
        h.Probe.Alive.Add(100);
        h.Tracker.Start(Id, @"C:\Games\Foo", 100);
        h.Clock.Now += TimeSpan.FromSeconds(30);
        await h.Tracker.PollAsync();
        Assert.Equal(TimeSpan.FromSeconds(30), h.Tracker.SessionTime(Id));
    }

    [Fact]
    public void StartingAGameThatIsAlreadyTrackedIsRefused()
    {
        var h = new Harness();
        Assert.True(h.Tracker.Start(Id, @"C:\Games\Foo", 100));
        Assert.False(h.Tracker.Start(Id, @"C:\Games\Foo", 101));
        Assert.Equal(1, h.Changes);
    }

    [Fact]
    public async Task NoProcessListIsTakenWithoutSessionsOrWhenThePidIsAlive()
    {
        var h = new Harness();
        await h.Tracker.PollAsync();
        Assert.Equal(0, h.Probe.SnapshotCalls);

        h.Probe.Alive.Add(100);
        h.Tracker.Start(Id, @"C:\Games\Foo", 100);
        await h.StepAsync(2);
        Assert.Equal(0, h.Probe.SnapshotCalls);

        h.Probe.Alive.Clear();
        await h.StepAsync(1);
        Assert.Equal(1, h.Probe.SnapshotCalls);
    }

    // ----- path matching ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Games\Foo", @"C:\Games\Foo\game.exe", true)]
    [InlineData(@"C:\Games\Foo", @"C:\Games\Foo\bin\x64\game.exe", true)]
    [InlineData(@"C:\Games\Foo", @"C:\Games\FooBar\game.exe", false)]
    [InlineData(@"C:\Games\Foo", @"C:\Games\Fo\game.exe", false)]
    [InlineData(@"C:\Games\Foo", @"C:\Games\Foo", false)]                  // the folder itself is not a program
    [InlineData(@"C:\Games\Foo\", @"C:\Games\Foo\game.exe", true)]         // trailing separator
    [InlineData(@"C:\Games\Foo\", @"C:\Games\FooBar\game.exe", false)]
    [InlineData(@"c:\GAMES\foo", @"C:\Games\FOO\Game.EXE", true)]          // case-insensitive
    [InlineData(@"C:\Games\Foo", @"C:\Games\Foo\..\Bar\game.exe", false)]  // normalized before comparing
    [InlineData(@"C:\Games\Foo\bin\..", @"C:\Games\Foo\game.exe", true)]
    [InlineData(@"C:\Games\Foo", @"D:\Games\Foo\game.exe", false)]
    public async Task ProgramsAreMatchedByWholeFolderNames(string installDir, string processPath, bool expected)
    {
        var h = new Harness();
        h.Tracker.Start(Id, installDir, null);
        h.Probe.Processes.Add((50, processPath));
        await h.StepAsync(1);
        Assert.Equal(expected, h.Tracker.SessionTime(Id) > TimeSpan.Zero);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    [InlineData(@"d:\")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"C:\Apps")]                    // contains Magpienest's own program
    [InlineData(@"C:\Apps\Magpienest")]
    [InlineData(@"C:\Apps\Magpienest\")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FolderMatchingIsOffForRootsShortPathsAndTheFolderOfMagpienestItself(string installDir)
    {
        Assert.Null(PlaySessionTracker.DirectoryPrefix(installDir, Own));

        var h = new Harness();
        h.Probe.Processes.Add((50, installDir.Length > 3 ? Path.Combine(installDir, "game.exe") : @"C:\game.exe"));
        h.Probe.Processes.Add((51, @"C:\Games\Foo\game.exe"));

        // Without a process id nothing can be followed ...
        h.Tracker.Start(Id, installDir, null);
        await h.StepAsync(1);
        Assert.Equal(TimeSpan.Zero, h.Tracker.SessionTime(Id));
        Assert.Equal(0, h.Probe.SnapshotCalls);
        await h.Tracker.StopAsync(Id);

        // ... with one, the process id alone counts.
        h.Probe.Alive.Add(100);
        h.Tracker.Start(Id, installDir, 100);
        await h.StepAsync(1);
        Assert.Equal(TimeSpan.FromSeconds(2), h.Tracker.SessionTime(Id));
    }

    [Theory]
    [InlineData(@"C:\Games\Foo")]
    [InlineData(@"C:\Games\Foo\")]
    [InlineData(@"C:\Apps\Other")]
    [InlineData(@"C:\Ap")]                      // four characters or more, not a root
    public void FolderMatchingIsOnForOrdinaryGameFolders(string installDir)
    {
        Assert.NotNull(PlaySessionTracker.DirectoryPrefix(installDir, Own));
    }

    [Fact]
    public void TheFolderPrefixEndsWithOneSeparator()
    {
        Assert.Equal(@"C:\Games\Foo\", PlaySessionTracker.DirectoryPrefix(@"C:\Games\Foo\", null));
        Assert.Equal(@"C:\Games\Foo\", PlaySessionTracker.DirectoryPrefix(@"C:\Games\Foo", null));
    }

    // ----- persistence ------------------------------------------------------------------------------------------

    [Fact]
    public async Task PeriodicSavesAddUpExactlyToTheWholeSecondsPlayed()
    {
        var h = new Harness();
        h.Probe.Alive.Add(100);
        h.Tracker.Start(Id, @"C:\Games\Foo", 100);

        // 77 polls of 1.9 s = 146.3 s of play; a save is due once 60 s have passed since the last one.
        for (var i = 0; i < 77; i++)
        {
            await h.StepAsync(1, TimeSpan.FromMilliseconds(1900));
        }

        Assert.True(h.Saves.Count >= 2, "a long session is saved while it runs");
        var duringSession = h.TotalSaved(Id);
        Assert.InRange(duringSession, 1, 146);

        h.Probe.Alive.Clear();
        await h.StepAsync(3, TimeSpan.FromMilliseconds(1900));
        Assert.False(h.Tracker.IsTracking(Id));
        Assert.Equal(146, h.TotalSaved(Id));
        Assert.All(h.Saves.SkipLast(1), s => Assert.True(s.Seconds > 0));
    }

    [Fact]
    public async Task NothingIsSavedBeforeSixtySecondsHavePassed()
    {
        var h = new Harness();
        h.Probe.Alive.Add(100);
        h.Tracker.Start(Id, @"C:\Games\Foo", 100);
        await h.StepAsync(29);                     // 58 s
        Assert.Empty(h.Saves);
        await h.StepAsync(1);                      // 60 s
        Assert.Equal([(Id, 60L)], h.Saves);
    }

    [Fact]
    public async Task StoppingByHandStoresTheTimeAndEndsTheSession()
    {
        var h = new Harness();
        h.Probe.Alive.Add(100);
        h.Tracker.Start(Id, @"C:\Games\Foo", 100);
        await h.StepAsync(5);
        await h.Tracker.StopAsync(Id);

        Assert.False(h.Tracker.IsTracking(Id));
        Assert.Equal([(Id, 10L)], h.Saves);
        Assert.Equal(2, h.Changes);

        await h.StepAsync(3);                      // nothing left to follow
        await h.Tracker.StopAsync(Id);             // stopping twice is harmless
        Assert.Single(h.Saves);
    }

    [Fact]
    public async Task FlushingAtShutdownStoresEveryRunningGameAndLeavesNothingForLater()
    {
        var h = new Harness();
        h.Probe.Alive.UnionWith([100, 101]);
        h.Tracker.Start(1, @"C:\Games\A", 100);
        h.Tracker.Start(2, @"C:\Games\B", 101);
        await h.StepAsync(5, TimeSpan.FromMilliseconds(2500)); // 12.5 s each

        await h.Tracker.FlushAllAsync();
        Assert.Equal(12, h.TotalSaved(1));
        Assert.Equal(12, h.TotalSaved(2));
        Assert.True(h.Tracker.IsTracking(1));

        await h.Tracker.FlushAllAsync();           // nothing new
        Assert.Equal(2, h.Saves.Count);

        await h.StepAsync(1, TimeSpan.FromMilliseconds(2500)); // 15 s
        await h.Tracker.StopAsync(1);
        Assert.Equal(15, h.TotalSaved(1));         // only the rest was added
    }

    [Fact]
    public async Task AFailedSaveIsRetriedWithoutLosingOrDoublingTime()
    {
        var h = new Harness { FailNextSave = true };
        h.Probe.Alive.Add(100);
        h.Tracker.Start(Id, @"C:\Games\Foo", 100);
        await h.StepAsync(30);                     // 60 s: the save fails
        Assert.Empty(h.Saves);

        await h.StepAsync(30);                     // 120 s: the next save is due and carries everything
        Assert.Equal(120, h.TotalSaved(Id));
    }

    [Fact]
    public void SessionTimeIsShownAsHoursMinutesSeconds()
    {
        Assert.Equal("0:00:00", GameFormat.SessionTime(TimeSpan.Zero));
        Assert.Equal("0:01:05", GameFormat.SessionTime(TimeSpan.FromSeconds(65.9)));
        Assert.Equal("26:03:09", GameFormat.SessionTime(new TimeSpan(26, 3, 9)));
        Assert.Equal("0:00:00", GameFormat.SessionTime(TimeSpan.FromSeconds(-4)));
    }

    // ----- command line -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { "--import-games", @"D:\Games" }, @"D:\Games")]
    [InlineData(new[] { "--smoke-test", "--IMPORT-GAMES", "x y", "--lang" }, "x y")]
    [InlineData(new[] { "--import-games" }, null)]
    [InlineData(new[] { "--import-games", " " }, null)]
    [InlineData(new string[0], null)]
    public void TheImportFolderIsReadFromTheCommandLine(string[] args, string? expected)
    {
        Assert.Equal(expected, GameCommandLine.ImportFolder(args));
    }

    [Fact]
    public void OnlyReadyFoldersAreImportedWithTheirBestProgramAndFolderName()
    {
        GameExecutable Exe(string path) => new(path, Path.GetFileName(path), 1, 0);
        var ready = new GameCandidate(@"D:\G\Star Harbor", "Star Harbor", GameCandidateStatus.Ready,
            [Exe(@"D:\G\Star Harbor\a.exe"), Exe(@"D:\G\Star Harbor\b.exe")]);
        var none = new GameCandidate(@"D:\G\Empty", "Empty", GameCandidateStatus.NoExecutable, []);
        var known = new GameCandidate(@"D:\G\Known", "Known", GameCandidateStatus.AlreadyInLibrary, [Exe(@"D:\G\Known\k.exe")]);

        var items = GameCommandLine.SelectImports([none, ready, known]);

        var item = Assert.Single(items);
        Assert.Equal("game", item.Kind);
        Assert.Equal(@"D:\G\Star Harbor\a.exe", item.Path);
        Assert.Equal("Star Harbor", item.Title);
        var extra = LibraryItemExtensions.GetExtra<GameExtra>(new LibraryItem
        {
            Kind = "game",
            Path = item.Path,
            Directory = @"D:\G\Star Harbor",
            Title = item.Title,
            Extra = item.Extra,
        });
        Assert.Equal(@"D:\G\Star Harbor", extra.InstallDir);
    }

    // ----- fakes ------------------------------------------------------------------------------------------------

    private sealed class FakeProbe : IProcessProbe
    {
        public HashSet<int> Alive { get; } = [];

        public List<(int Pid, string Path)> Processes { get; } = [];

        public int SnapshotCalls { get; private set; }

        public IReadOnlyList<(int Pid, string Path)> Snapshot()
        {
            SnapshotCalls++;
            return [.. Processes];
        }

        public bool IsRunning(int pid) => Alive.Contains(pid);
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public TimeSpan Now { get; set; } = TimeSpan.FromMinutes(5);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Tracker = new PlaySessionTracker(Probe, Clock, SaveAsync, Own);
            Tracker.SessionsChanged += () => Changes++;
        }

        public FakeProbe Probe { get; } = new();

        public FakeClock Clock { get; } = new();

        public PlaySessionTracker Tracker { get; }

        public List<(long Id, long Seconds)> Saves { get; } = [];

        public int Changes { get; private set; }

        public bool FailNextSave { get; set; }

        public long TotalSaved(long id) => Saves.Where(s => s.Id == id).Sum(s => s.Seconds);

        /// <summary>Lets time pass and polls, <paramref name="polls"/> times.</summary>
        public async Task StepAsync(int polls, TimeSpan? interval = null)
        {
            for (var i = 0; i < polls; i++)
            {
                Clock.Now += interval ?? PlaySessionTracker.PollInterval;
                await Tracker.PollAsync();
            }
        }

        private Task SaveAsync(long id, long seconds)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("disk full");
            }

            Saves.Add((id, seconds));
            return Task.CompletedTask;
        }
    }
}

/// <summary>The real process probe, and the tracker on top of it with the FakeGame tool.</summary>
public sealed class RealProcessTests
{
    [Fact]
    public void TheRealProbeSeesTheCurrentProcess()
    {
        var probe = new ProcessProbe();
        var self = Environment.ProcessId;
        Assert.True(probe.IsRunning(self));
        Assert.False(probe.IsRunning(int.MaxValue));

        var own = Environment.ProcessPath!;
        Assert.Contains(probe.Snapshot(), p => p.Pid == self && string.Equals(p.Path, own, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AGameStartedThroughALauncherThatExitsIsFollowedToTheEnd()
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "tools", "FakeGame"));
        var exe = Path.Combine(folder, "FakeGame.exe");
        Assert.SkipUnless(File.Exists(exe), "tools/FakeGame has not been built.");

        var saved = 0L;
        var tracker = new PlaySessionTracker(new ProcessProbe(), new StopwatchClock(), (_, s) =>
        {
            Interlocked.Add(ref saved, s);
            return Task.CompletedTask;
        }, Environment.ProcessPath);
        var probe = new ProcessProbe();

        using var launcher = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--spawn-child",
            UseShellExecute = true,
        })!;
        var pid = launcher.Id;
        tracker.Start(1, folder, pid);
        try
        {
            // Wait until the launcher is gone and the child is the only thing left.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline && (probe.IsRunning(pid) || !probe.Snapshot().Any(p => IsFakeGame(p.Path, exe))))
            {
                await tracker.PollAsync();
                await Task.Delay(200, TestContext.Current.CancellationToken);
            }

            await tracker.PollAsync();
            var before = tracker.SessionTime(1);
            Assert.NotNull(before);
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(300, TestContext.Current.CancellationToken);
                await tracker.PollAsync();
            }

            Assert.True(tracker.IsTracking(1), "still followed after the launcher exited");
            Assert.True(tracker.SessionTime(1) > before);
        }
        finally
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("FakeGame"))
            {
                using (process)
                {
                    try
                    {
                        if (IsFakeGame(process.MainModule?.FileName, exe))
                        {
                            process.Kill();
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }
        }

        var end = DateTime.UtcNow.AddSeconds(20);
        while (tracker.IsTracking(1) && DateTime.UtcNow < end)
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            await tracker.PollAsync();
        }

        Assert.False(tracker.IsTracking(1));
        Assert.True(Interlocked.Read(ref saved) >= 1);
    }

    private static bool IsFakeGame(string? path, string exe) => string.Equals(path, exe, StringComparison.OrdinalIgnoreCase);
}
