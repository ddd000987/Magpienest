using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using Magpienest.Sdk;

namespace Magpienest.Plugins.MediaEngine.LibVlc.Tests;

public sealed class LibVlcEngineTests
{
    [Theory]
    [InlineData(1920, 1080, 320, 320u, 180u)]
    [InlineData(1080, 1920, 320, 180u, 320u)]
    [InlineData(200, 100, 320, 200u, 100u)]
    [InlineData(0, 0, 320, 320u, 180u)]
    public void CaptureSizeKeepsTheAspectAndNeverUpscales(int width, int height, int max, uint w, uint h) =>
        Assert.Equal((w, h), LibVlcEngine.CaptureSize(width, height, max));

    [Fact]
    public async Task AFileThatIsNotAVideoGivesNoFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = new LibVlcEngine(NativeDirectory(), new NullLog());
        try
        {
            var file = Path.Combine(Path.GetTempPath(), $"magpienest-{Guid.NewGuid():N}.mp4");
            await File.WriteAllTextAsync(file, "not a video", ct);
            try
            {
                var info = await engine.ProbeAsync(file, ct);
                Assert.True(info is null || info.VideoWidth == 0);
                Assert.Null(await engine.CaptureFrameAsync(file, 0.1, 160, ct));
            }
            finally
            {
                File.Delete(file);
            }

            Assert.StartsWith("LibVLC 3.0.", engine.Name);
        }
        finally
        {
            engine.Dispose();
        }
    }

    [Fact]
    public async Task AShortVideoIsProbedAndAFrameIsCaptured()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("magpienest-video-").FullName;
        var engine = new LibVlcEngine(NativeDirectory(), new NullLog());
        try
        {
            var video = await MakeVideoAsync(engine.Vlc, dir, ct);
            Assert.SkipWhen(video is null, "LibVLC could not encode a test video on this machine.");

            var info = await engine.ProbeAsync(video!, ct);
            Assert.NotNull(info);
            Assert.Equal(64, info.VideoWidth);
            Assert.Equal(48, info.VideoHeight);
            Assert.InRange(info.Duration.TotalSeconds, 0.5, 5);

            var frame = await engine.CaptureFrameAsync(video!, 0.1, 32, ct);
            Assert.NotNull(frame);
            Assert.Equal(32, frame.PixelWidth);
            Assert.Equal(24, frame.PixelHeight);
            var pixel = new byte[4];
            frame.CopyPixels(new System.Windows.Int32Rect(16, 12, 1, 1), pixel, 4, 0);

            // The source picture is pure red (BGR order); lossy encoding keeps it close.
            Assert.InRange(pixel[2], 180, 255);
            Assert.InRange(pixel[1], 0, 80);
            Assert.InRange(pixel[0], 0, 80);
        }
        finally
        {
            engine.Dispose();
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void PlayerEventsReachTheUiThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("magpienest-player-").FullName;
        var engine = new LibVlcEngine(NativeDirectory(), new NullLog());
        Exception? failure = null;
        var states = 0;
        var positions = 0;
        var ended = false;
        var playingSeen = false;
        var thread = new Thread(() =>
        {
            try
            {
                var video = MakeVideoAsync(engine.Vlc, dir, ct).GetAwaiter().GetResult();
                if (video is null)
                {
                    return;
                }

                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                var player = new LibVlcPlayer(engine, new NullLog()) { MediaOptions = [":no-video"] };
                player.StateChanged += (_, _) =>
                {
                    states++;
                    playingSeen |= player.IsPlaying;
                };
                player.PositionChanged += (_, _) => positions++;
                player.EndReached += (_, _) =>
                {
                    ended = true;
                    dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
                };
                player.Open(video);
                var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                timeout.Tick += (_, _) => dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
                timeout.Start();
                System.Windows.Threading.Dispatcher.Run();
                player.Dispose();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        engine.Dispose();
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }

        Assert.Null(failure);
        // (Without video or audio output the clip is decoded faster than real time, so LibVLC sends no time
        // events here; they travel the same Forward path as the state events checked below.)
        Assert.True(playingSeen, $"never playing ({states} state events, {positions} position events)");
        Assert.True(ended, "end not reached");
    }

    [Fact]
    public void ReplayingAfterTheEndStaysInTheView()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("magpienest-replay-").FullName;
        var engine = new LibVlcEngine(NativeDirectory(), new NullLog());
        Exception? failure = null;
        var ends = 0;
        var strayWindows = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                var video = MakeVideoAsync(engine.Vlc, dir, ct).GetAwaiter().GetResult();
                if (video is null)
                {
                    return;
                }

                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                var player = new LibVlcPlayer(engine, new NullLog());
                var window = new System.Windows.Window
                {
                    Width = 160,
                    Height = 120,
                    Left = -20000,
                    Top = -20000,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    Content = player.CreateView(null),
                };
                window.Show();
                var check = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                check.Tick += (_, _) => strayWindows.AddRange(
                    VlcOutputWindows().Select(w => $"{w} (after {ends} ends)").Except(strayWindows));
                check.Start();
                player.EndReached += (_, _) =>
                {
                    if (++ends == 1)
                    {
                        player.Play();   // play again after the end
                    }
                    else
                    {
                        dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
                    }
                };
                player.Open(video);
                var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                timeout.Tick += (_, _) => dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
                timeout.Start();
                System.Windows.Threading.Dispatcher.Run();
                check.Stop();
                player.Dispose();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        engine.Dispose();
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }

        Assert.Null(failure);
        Assert.Equal(2, ends);
        Assert.Empty(strayWindows);
    }

    /// <summary>Top-level windows of this process that LibVLC opened itself ("VLC (… output)").</summary>
    private static List<string> VlcOutputWindows()
    {
        var found = new List<string>();
        var pid = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid && IsWindowVisible(hwnd))
            {
                var text = new System.Text.StringBuilder(256);
                GetWindowText(hwnd, text, text.Capacity);
                if (text.ToString().StartsWith("VLC", StringComparison.Ordinal))
                {
                    found.Add(text.ToString());
                }
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);

    [Fact]
    public async Task AudioAtAnotherRateUsesTheGoodResampler()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Directory.CreateTempSubdirectory("magpienest-audio-").FullName;
        var engine = new LibVlcEngine(NativeDirectory(), new NullLog());
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        EventHandler<LogEventArgs> log = (_, e) => messages.Enqueue(e.Message);
        try
        {
            // 44.1 kHz in, 48 kHz out: the output has to resample, as with most music videos on most PCs.
            var wav = Path.Combine(dir, "tone.wav");
            await File.WriteAllBytesAsync(wav, ToneWav(44_100, TimeSpan.FromSeconds(1)), ct);
            var vlc = engine.Vlc;
            vlc.Log += log;
            using var media = new Media(vlc, wav, FromType.FromPath);
            using var player = new LibVLCSharp.Shared.MediaPlayer(media);
            LibVLCSharp.Shared.MediaPlayer.LibVLCAudioPlayCb play = (_, _, _, _) => { };
            player.SetAudioFormat("S16N", 48_000, 2);
            player.SetAudioCallbacks(play, null, null, null, null);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            player.EndReached += (_, _) => done.TrySetResult();
            player.EncounteredError += (_, _) => done.TrySetResult();
            Assert.True(player.Play());
            await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10), ct));
            await Task.Run(player.Stop, ct);
            GC.KeepAlive(play);
            vlc.Log -= log;

            var used = messages.Where(m => m.Contains("resampler", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(used.Any(m => m.Contains("speex_resampler", StringComparison.Ordinal)),
                "resampler messages: " + string.Join(" | ", used));
            Assert.DoesNotContain(used, m => m.Contains("ugly", StringComparison.Ordinal));
        }
        finally
        {
            engine.Dispose();
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A 16-bit stereo 440 Hz sine wave as a WAV file.</summary>
    private static byte[] ToneWav(int rate, TimeSpan length)
    {
        var frames = (int)(rate * length.TotalSeconds);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 4);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);       // PCM
        writer.Write((short)2);       // channels
        writer.Write(rate);
        writer.Write(rate * 4);       // bytes per second
        writer.Write((short)4);       // block align
        writer.Write((short)16);      // bits per sample
        writer.Write("data"u8);
        writer.Write(frames * 4);
        for (var i = 0; i < frames; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 8000);
            writer.Write(sample);
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Encodes a 2-second 64×48 red video from a still picture with LibVLC itself.</summary>
    private static async Task<string?> MakeVideoAsync(LibVLC vlc, string dir, CancellationToken ct)
    {
        var png = Path.Combine(dir, "red.png");
        var bitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgr32, null,
            Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 64 * 48).SelectMany(p => p).ToArray(), 64 * 4);
        await using (var stream = File.Create(png))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
        }

        var output = Path.Combine(dir, "red.mp4");
        using var media = new Media(vlc, png, FromType.FromPath);
        media.AddOption(":image-duration=2");
        media.AddOption(":image-fps=10");
        media.AddOption($":sout=#transcode{{vcodec=mp4v,vb=400,fps=10}}:std{{access=file,mux=mp4,dst='{output}'}}");
        media.AddOption(":sout-keep");
        using var player = new LibVLCSharp.Shared.MediaPlayer(media);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.EndReached += (_, _) => done.TrySetResult();
        player.EncounteredError += (_, _) => done.TrySetResult();
        if (!player.Play())
        {
            return null;
        }

        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20), ct));
        await Task.Run(player.Stop, ct);
        return File.Exists(output) && new FileInfo(output).Length > 0 ? output : null;
    }

    private static string NativeDirectory()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        if (Directory.Exists(local))
        {
            return local;
        }

        foreach (var config in new[] { "Debug", "Release" })
        {
            var plugin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "bin", config, "plugins",
                "magpienest.mediaengine.libvlc", "libvlc", "win-x64"));
            if (Directory.Exists(plugin))
            {
                return plugin;
            }
        }

        throw new DirectoryNotFoundException("The LibVLC native libraries were not found.");
    }

    private sealed class NullLog : ILog
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message, Exception? ex = null) { }
        public void Error(string message, Exception? ex = null) { }
    }
}
