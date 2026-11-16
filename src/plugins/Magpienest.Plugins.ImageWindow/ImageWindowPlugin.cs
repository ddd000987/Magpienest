using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Settings;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.ImageWindow;

/// <summary>Provides <see cref="IImageWindowService"/>: pictures in their own borderless windows.</summary>
[Requires(typeof(IShellService), typeof(ISettingsService), typeof(IImagingService))]
public sealed class ImageWindowPlugin : Plugin
{
    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var service = new ImageWindowService(ctx.Ui, ctx.Get<IImagingService>(), ctx.Config, ctx.Log);
        ctx.Track(service.CloseAll);
        ctx.Provide<IImageWindowService>(service);
        ctx.Get<ISettingsService>().AddSection(ctx, new SettingsSection
        {
            Id = "imagewindow",
            TitleKey = "ImageWindow.Settings.Title",
            Glyph = "",
            Order = 210,
            CreateView = () => new ImageWindowSettingsView { DataContext = new ImageWindowSettingsViewModel(ctx.Config) },
        });
    }
}

/// <summary>Reads the image into memory, decodes it off the UI thread and shows an <see cref="ImageWindow"/>.</summary>
internal sealed class ImageWindowService(IUiDispatcher ui, IImagingService imaging, IPluginConfig config, ILog log,
                                         TimeProvider? clock = null)
    : IImageWindowService
{
    internal const string TopmostKey = "topmost";

    /// <summary>Windows opened within this time of each other are cascaded.</summary>
    private static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(1);

    private readonly List<ImageWindow> _windows = [];   // UI thread only
    private DateTime _lastShown = DateTime.MinValue;
    private int _cascade;

    /// <summary>Largest image file accepted.</summary>
    internal long MaxBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Open windows (UI thread only).</summary>
    internal IReadOnlyList<ImageWindow> Windows => _windows;

    /// <summary>Position of the last window in its batch (0 = first).</summary>
    internal int Cascade => _cascade;

    public async Task OpenAsync(ImageWindowRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = await ReadAllAsync(request, MaxBytes, ct).ConfigureAwait(false);
        var info = await imaging.ReadInfoAsync(new MemoryStream(bytes, writable: false), ct).ConfigureAwait(false);
        var monitor = NativeMethods.MonitorAt(NativeMethods.CursorPosition());
        var cap = (int)Math.Min(int.MaxValue / 4, 2 * Math.Max(monitor.Bounds.Width, monitor.Bounds.Height));
        var bitmap = await imaging.DecodeAsync(new MemoryStream(bytes, writable: false), new DecodeOptions(cap, cap), ct)
                                  .ConfigureAwait(false);
        var size = info is { PixelWidth: > 0, PixelHeight: > 0 }
            ? new Size(info.PixelWidth, info.PixelHeight)
            : new Size(bitmap.PixelWidth, bitmap.PixelHeight);
        ct.ThrowIfCancellationRequested();
        await ui.InvokeAsync(() => Show(request, bitmap, size));
    }

    /// <summary>Closes every window (plugin deactivation, app exit).</summary>
    public void CloseAll()
    {
        foreach (var window in _windows.ToList())
        {
            window.Close();
        }

        _windows.Clear();
    }

    private void Show(ImageWindowRequest request, BitmapSource bitmap, Size size)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        _cascade = now - _lastShown < BatchWindow ? _cascade + 1 : 0;
        _lastShown = now;
        var cursor = NativeMethods.CursorPosition();
        var bounds = WindowGeometry.InitialBounds(size, NativeMethods.MonitorAt(cursor).WorkArea, cursor, _cascade);
        var window = new ImageWindow(bitmap, size, request, bounds, config.Get(TopmostKey, false), log);
        window.Closed += (_, _) => _windows.Remove(window);
        _windows.Add(window);
        window.Show();
        window.Activate();
    }

    /// <summary>Copies the whole stream into memory so the window never depends on its source again.</summary>
    internal static async Task<byte[]> ReadAllAsync(ImageWindowRequest request, long maxBytes, CancellationToken ct)
    {
        var stream = await request.Open(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            if (stream.CanSeek && stream.Length > maxBytes)
            {
                throw new IOException($"The image is larger than {maxBytes / (1024 * 1024)} MB.");
            }

            using var buffer = stream.CanSeek ? new MemoryStream((int)stream.Length) : new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new IOException($"The image is larger than {maxBytes / (1024 * 1024)} MB.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }
}

/// <summary>Settings section "imagewindow".</summary>
internal sealed partial class ImageWindowSettingsViewModel : ObservableObject
{
    private readonly IPluginConfig _config;

    public ImageWindowSettingsViewModel(IPluginConfig config)
    {
        _config = config;
        Topmost = config.Get(ImageWindowService.TopmostKey, false);
    }

    [ObservableProperty]
    public partial bool Topmost { get; set; }

    partial void OnTopmostChanged(bool value) => _config.Set(ImageWindowService.TopmostKey, value);
}

/// <summary>View of the "imagewindow" settings section.</summary>
internal sealed partial class ImageWindowSettingsView : UserControl
{
    public ImageWindowSettingsView() => InitializeComponent();
}
