using System.Windows;
using System.Windows.Media.Imaging;

namespace Magpienest.Sdk.Media;

/// <summary>Provided by a media engine plugin: playback, probing and frame capture for audio/video files.</summary>
public interface IMediaEngine
{
    /// <summary>Engine name and version, for example "LibVLC 3.0.21".</summary>
    string Name { get; }

    /// <summary>Creates a player. Call on the UI thread; the caller disposes it.</summary>
    IMediaPlayer CreatePlayer();

    /// <summary>Reads duration and video size; null when the file cannot be parsed.</summary>
    /// <param name="path">Media file path.</param>
    /// <param name="ct">Cancellation.</param>
    Task<MediaInfo?> ProbeAsync(string path, CancellationToken ct = default);

    /// <summary>A single video frame for thumbnails; null when it cannot be produced.</summary>
    /// <param name="path">Media file path.</param>
    /// <param name="positionFraction">Where to take the frame, 0..1 of the duration.</param>
    /// <param name="maxPixelSize">Upper bound of the longer side in pixels.</param>
    /// <param name="ct">Cancellation.</param>
    Task<BitmapSource?> CaptureFrameAsync(string path, double positionFraction, int maxPixelSize,
                                          CancellationToken ct = default);
}

/// <summary>Basic facts about a media file.</summary>
/// <param name="Duration">Length; zero when unknown.</param>
/// <param name="VideoWidth">Width of the first video track; zero without video.</param>
/// <param name="VideoHeight">Height of the first video track; zero without video.</param>
public sealed record MediaInfo(TimeSpan Duration, int VideoWidth, int VideoHeight);

/// <summary>An audio or subtitle track.</summary>
/// <param name="Id">Engine track id.</param>
/// <param name="Name">Display name as reported by the file.</param>
public sealed record MediaTrack(int Id, string Name);

/// <summary>A media player. All members are called on the UI thread. All events are raised on the UI thread.</summary>
public interface IMediaPlayer : IDisposable
{
    /// <summary>
    /// Call exactly once. <paramref name="overlay"/> is drawn above the video and receives input.
    /// Anything that must appear over the video MUST be inside it (a native video surface hides normal WPF
    /// content placed on top of it).
    /// </summary>
    /// <param name="overlay">Content drawn above the video.</param>
    FrameworkElement CreateView(FrameworkElement? overlay);

    /// <summary>Opens a file and starts playing.</summary>
    /// <param name="path">Media file path.</param>
    /// <param name="startAt">Start position; null for the beginning.</param>
    void Open(string path, TimeSpan? startAt = null);

    /// <summary>Resumes playback.</summary>
    void Play();

    /// <summary>Pauses playback.</summary>
    void Pause();

    /// <summary>Stops playback and releases the media.</summary>
    void Stop();

    /// <summary>Whether the media is playing.</summary>
    bool IsPlaying { get; }

    /// <summary>Whether a media is open.</summary>
    bool HasMedia { get; }

    /// <summary>Playback position.</summary>
    TimeSpan Position { get; set; }

    /// <summary>Media length; zero when unknown.</summary>
    TimeSpan Duration { get; }

    /// <summary>Volume, 0..100.</summary>
    int Volume { get; set; }

    /// <summary>Whether the sound is muted.</summary>
    bool IsMuted { get; set; }

    /// <summary>Playback speed, 0.25..4.0.</summary>
    double Rate { get; set; }

    /// <summary>Audio tracks of the open media.</summary>
    IReadOnlyList<MediaTrack> AudioTracks { get; }

    /// <summary>Subtitle tracks of the open media; does not include "off".</summary>
    IReadOnlyList<MediaTrack> SubtitleTracks { get; }

    /// <summary>Current audio track id.</summary>
    int? CurrentAudioTrack { get; set; }

    /// <summary>Current subtitle track id; null = subtitles off.</summary>
    int? CurrentSubtitleTrack { get; set; }

    /// <summary>Adds an external subtitle file and selects it.</summary>
    /// <param name="path">Subtitle file path.</param>
    bool AddSubtitleFile(string path);

    /// <summary>Play/pause, tracks or duration changed.</summary>
    event EventHandler? StateChanged;

    /// <summary>The position changed; at most 4 times per second.</summary>
    event EventHandler? PositionChanged;

    /// <summary>Playback reached the end.</summary>
    event EventHandler? EndReached;

    /// <summary>Playback failed; the argument is a technical message for the log.</summary>
    event EventHandler<string>? Failed;
}
