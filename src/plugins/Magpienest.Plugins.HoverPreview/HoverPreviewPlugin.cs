using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Settings;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.HoverPreview;

/// <summary>Provides <see cref="IHoverPreviewService"/>: a large preview beside the cursor while it rests on a picture.</summary>
[Requires(typeof(ISettingsService), typeof(IImagingService))]
public sealed class HoverPreviewPlugin : Plugin
{
    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var service = new HoverPreviewService(ctx.Get<IImagingService>(), ctx.Config, ctx.Log);
        ctx.Track(service);
        ctx.Provide<IHoverPreviewService>(service);
        ctx.Get<ISettingsService>().AddSection(ctx, new SettingsSection
        {
            Id = "hoverpreview",
            TitleKey = "HoverPreview.Settings.Title",
            Glyph = "",
            Order = 205,
            CreateView = () => new HoverPreviewSettingsView { DataContext = new HoverPreviewSettingsViewModel(ctx.Config) },
        });
    }
}

/// <summary>A choice in a settings list.</summary>
internal sealed record DelayChoice(int Milliseconds, string Key)
{
    public string Label => Loc.Get(Key);
}

/// <summary>Settings section "hoverpreview".</summary>
internal sealed partial class HoverPreviewSettingsViewModel : ObservableObject
{
    private readonly IPluginConfig _config;

    public HoverPreviewSettingsViewModel(IPluginConfig config)
    {
        _config = config;
        var stored = config.Get(HoverPreviewService.DelayKey, HoverPreviewService.DefaultDelay);
        Delay = Delays.MinBy(d => Math.Abs(d.Milliseconds - stored))!;
    }

    public IReadOnlyList<DelayChoice> Delays { get; } =
    [
        new(200, "HoverPreview.Delay.Short"),
        new(400, "HoverPreview.Delay.Normal"),
        new(700, "HoverPreview.Delay.Long"),
        new(1000, "HoverPreview.Delay.VeryLong"),
    ];

    [ObservableProperty]
    public partial DelayChoice Delay { get; set; }

    partial void OnDelayChanged(DelayChoice value) => _config.Set(HoverPreviewService.DelayKey, value.Milliseconds);
}

/// <summary>View of the "hoverpreview" settings section.</summary>
internal sealed partial class HoverPreviewSettingsView : UserControl
{
    public HoverPreviewSettingsView() => InitializeComponent();
}
