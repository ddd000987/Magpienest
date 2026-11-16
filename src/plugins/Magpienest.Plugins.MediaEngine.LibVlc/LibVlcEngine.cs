using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using Magpienest.Sdk;
using Magpienest.Sdk.Media;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Magpienest.Plugins.MediaEngine.LibVlc;

/// <summary>
/// <see cref="IMediaEngine"/> on LibVLC. One <see cref="LibVLC"/> instance for the plugin lifetime, created on
/// first use; it is released only after every object handed to the background has been released.
/// </summary>
internal sealed class LibVlcEngine : IMediaEngine, IDisposable
{
    /// <summary>How long a frame capture may wait for the first picture.</summary>
    public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How long probing may take.</summary>
    public const int ProbeTimeoutMs = 5000;

    // The NuGet build lacks the libsamplerate resampler desktop VLC uses; without a choice LibVLC then resamples
    // with "ugly" (audibly harsh when a 44.1 kHz track plays on a 48 kHz device). Speex is good and bundled.
    private static readonly string[] Options =
    [
        "--no-osd", "--no-video-title-show", "--no-snapshot-preview",
        "--audio-resampler=speex_resampler", "--speex-resampler-quality=8",
    ];

    private readonly string _nativeDirectory;
    private readonly ILog _log;
    private readonly Lazy<LibVLC> _vlc;
    private readonly Lazy<string> _name;
    private readonly SemaphoreSlim _capture = new(1, 1);
    private readonly Lock _pendingLock = new();
    private readonly List<Task> _pending = [];
    private bool _disposed;

    public LibVlcEngine(string nativeDirectory, ILog log)
    {
        _nativeDirectory = nativeDirectory;
        _log = log;
        _vlc = new Lazy<LibVLC>(Create, LazyThreadSafetyMode.ExecutionAndPublication);
        _name = new Lazy<string>(ReadName);
    }

    /// <inheritdoc />
    public string Name => _name.Value;

    /// <summary>The LibVLC instance (created on first access).</summary>
    internal LibVLC Vlc
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _vlc.Value;
        }
    }

    /// <inheritdoc />
    public IMediaPlayer CreatePlayer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new LibVlcPlayer(this, _log);
    }

    /// <inheritdoc />
    public Task<MediaInfo?> ProbeAsync(string path, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var media = new Media(Vlc, path, FromType.FromPath);
            try
            {
                return await ProbeAsync(media, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Debug($"Probing {path} failed: {ex.Message}");
                return null;
            }
            finally
            {
                _ = Release(media.Dispose, ParsedReleaseDelay);
            }
        }, ct);

    /// <inheritdoc />
    public async Task<BitmapSource?> CaptureFrameAsync(string path, double positionFraction, int maxPixelSize,
                                                       CancellationToken ct = default)
    {
        await _capture.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => CaptureAsync(path, positionFraction, maxPixelSize, ct), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Debug($"Capturing a frame of {path} failed: {ex.Message}");
            return null;
        }
        finally
        {
            _capture.Release();
        }
    }

    /// <summary>Runs a blocking LibVLC release (stop, dispose) on the thread pool; <see cref="Dispose"/> waits for it.</summary>
    /// <param name="release">Stops and disposes native objects.</param>
    /// <param name="delay">Wait this long first (see <see cref="ParsedReleaseDelay"/>).</param>
    internal Task Release(Action release, TimeSpan delay = default)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }

                release();
            }
            catch (Exception ex)
            {
                _log.Warn("Releasing LibVLC objects failed.", ex);
            }
        });

        lock (_pendingLock)
        {
            _pending.RemoveAll(t => t.IsCompleted);
            _pending.Add(task);
        }

        return task;
    }

    /// <summary>
    /// LibVLC 3 reports "parsed" from inside its preparser callback; releasing the media while that callback is
    /// still unwinding corrupts LibVLC (the next parse crashes the process, releasing LibVLC hangs). Media that has
    /// been parsed is therefore released a little later, off the caller's path.
    /// </summary>
    internal static readonly TimeSpan ParsedReleaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>Waits for pending releases in the background, then releases LibVLC. Does not block.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Task[] pending;
        lock (_pendingLock)
        {
            pending = [.. _pending];
        }

        var vlc = _vlc.IsValueCreated ? _vlc.Value : null;
        var log = _log;
        var capture = _capture;
        _ = Task.Run(async () =>
        {
            try
            {
                await capture.WaitAsync().ConfigureAwait(false);
                await Task.WhenAll(pending).ConfigureAwait(false);
                vlc?.Dispose();
            }
            catch (Exception ex)
            {
                log.Warn("Releasing LibVLC failed.", ex);
            }
        });
    }

    private LibVLC Create()
    {
        Core.Initialize(_nativeDirectory);
        var vlc = new LibVLC(Options);
        _log.Info($"LibVLC {vlc.Version} initialised from {_nativeDirectory}.");
        return vlc;
    }

    private string ReadName()
    {
        try
        {
            var file = Path.Combine(_nativeDirectory, "libvlc.dll");
            if (File.Exists(file) && FileVersionInfo.GetVersionInfo(file) is { FileMajorPart: > 0 } info)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"LibVLC {info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}");
            }
        }
        catch (IOException)
        {
        }

        return "LibVLC";
    }

    private static async Task<MediaInfo?> ProbeAsync(Media media, CancellationToken ct)
    {
        var status = await media.Parse(MediaParseOptions.ParseLocal, ProbeTimeoutMs, ct).ConfigureAwait(false);
        if (status != MediaParsedStatus.Done)
        {
            return null;
        }

        var (width, height) = DisplaySize(media);
        var duration = media.Duration > 0 ? TimeSpan.FromMilliseconds(media.Duration) : TimeSpan.Zero;
        return new MediaInfo(duration, width, height);
    }

    /// <summary>Size of the first video track as displayed (sample aspect ratio applied).</summary>
    private static (int Width, int Height) DisplaySize(Media media)
    {
        foreach (var track in media.Tracks)
        {
            if (track.TrackType != TrackType.Video)
            {
                continue;
            }

            var video = track.Data.Video;
            var width = (double)video.Width;
            if (video.SarNum > 0 && video.SarDen > 0 && video.SarNum != video.SarDen)
            {
                width = width * video.SarNum / video.SarDen;
            }

            return ((int)Math.Round(width), (int)video.Height);
        }

        return (0, 0);
    }

    /// <summary>Output size: the long side at most <paramref name="maxPixelSize"/>, never upscaled.</summary>
    internal static (uint Width, uint Height) CaptureSize(int width, int height, int maxPixelSize)
    {
        maxPixelSize = Math.Max(16, maxPixelSize);
        if (width <= 0 || height <= 0)
        {
            return ((uint)maxPixelSize, (uint)Math.Max(1, maxPixelSize * 9 / 16));
        }

        var scale = Math.Min(1.0, maxPixelSize / (double)Math.Max(width, height));
        return ((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)));
    }

    private async Task<BitmapSource?> CaptureAsync(string path, double fraction, int maxPixelSize,
                                                    CancellationToken ct)
    {
        var media = new Media(Vlc, path, FromType.FromPath);
        try
        {
            return await CaptureAsync(media, fraction, maxPixelSize, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = Release(media.Dispose, ParsedReleaseDelay);
        }
    }

    private static async Task<BitmapSource?> CaptureAsync(Media media, double fraction, int maxPixelSize,
                                                          CancellationToken ct)
    {
        if (await ProbeAsync(media, ct).ConfigureAwait(false) is not { VideoWidth: > 0 } info)
        {
            return null;
        }

        var (width, height) = CaptureSize(info.VideoWidth, info.VideoHeight, maxPixelSize);
        var pitch = width * 4;
        var size = (int)(pitch * height);
        var start = info.Duration.TotalSeconds * Math.Clamp(fraction, 0, 1);
        media.AddOption(":no-audio");
        if (start > 0)
        {
            media.AddOption(string.Create(CultureInfo.InvariantCulture, $":start-time={start:0.###}"));
        }

        var buffer = Marshal.AllocHGlobal(size);
        var pixels = new byte[size];
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The delegates must stay alive while LibVLC can call them (until the player is stopped).
        VlcPlayer.LibVLCVideoLockCb onLock = (_, planes) =>
        {
            Marshal.WriteIntPtr(planes, buffer);
            return IntPtr.Zero;
        };
        VlcPlayer.LibVLCVideoDisplayCb onDisplay = (_, _) =>
        {
            if (!ready.Task.IsCompleted)
            {
                Marshal.Copy(buffer, pixels, 0, size);
                ready.TrySetResult();
            }
        };

        var player = new VlcPlayer(media);
        try
        {
            player.SetVideoFormat("RV32", width, height, pitch);
            player.SetVideoCallbacks(onLock, null, onDisplay);
            if (!player.Play())
            {
                return null;
            }

            var finished = await Task.WhenAny(ready.Task, Task.Delay(CaptureTimeout, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (finished != ready.Task)
            {
                return null;
            }

            var bitmap = BitmapSource.Create((int)width, (int)height, 96, 96, PixelFormats.Bgr32, null, pixels,
                (int)pitch);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            player.Stop();
            player.Dispose();
            Marshal.FreeHGlobal(buffer);
            GC.KeepAlive(onLock);
            GC.KeepAlive(onDisplay);
        }
    }
}
