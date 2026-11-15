using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Magpienest.Sdk;
using Magpienest.Sdk.Imaging;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Settings;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Viewing;

namespace Magpienest.Plugins.ImageViewer;

/// <summary>Provides <see cref="IImageViewerService"/>: the full-window image viewer.</summary>
[Requires(typeof(IShellService), typeof(ISettingsService), typeof(IImagingService))]
[Optional(typeof(IImageWindowService))]
public sealed class ImageViewerPlugin : Plugin
{
    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        ctx.TryGet<IImageWindowService>(out var windows);
        var service = new ImageViewerService(ctx, ctx.Get<IShellService>(), ctx.Get<IImagingService>(), windows);
        ctx.Track(service.CloseCurrent);
        ctx.Provide<IImageViewerService>(service);
        ctx.On<LanguageChanged>(_ => service.RefreshTexts());
        ctx.Get<ISettingsService>().AddSection(ctx, new SettingsSection
        {
            Id = "viewer",
            TitleKey = "Viewer.Settings.Title",
            Glyph = "",
            Order = 200,
            CreateView = () => new ViewerSettingsView { DataContext = new ViewerSettingsViewModel(ctx.Config) },
        });
    }
}

/// <summary>Opens one viewer at a time as a Shell overlay.</summary>
internal sealed class ImageViewerService(IPluginContext ctx, IShellService shell, IImagingService imaging,
                                         IImageWindowService? windows) : IImageViewerService
{
    internal const string BudgetKey = "cacheBudgetMb";
    internal const string StretchKey = "stretchSmall";
    internal const int DefaultBudgetMb = 512;
    internal const string DirectionKey = "bookDirection";   // "rtl" | "ltr"
    internal const string SpreadKey = "bookSpread";         // "double" | "single"
    internal const string CoverAloneKey = "bookCoverAlone";
    internal const string BookFlowKey = "bookFlow";         // "paged" | "strip"
    internal const string GalleryFlowKey = "galleryFlow";   // "paged" | "strip"

    internal static BookSettings ReadBookDefaults(IPluginConfig config) => new(
        config.Get(DirectionKey, "rtl") == "ltr" ? ReadingDirection.LeftToRight : ReadingDirection.RightToLeft,
        config.Get(SpreadKey, "double") == "single" ? SpreadMode.Single : SpreadMode.Double,
        config.Get(CoverAloneKey, true),
        ReadFlow(config, BookFlowKey));

    internal static PageFlow ReadFlow(IPluginConfig config, string key) =>
        config.Get(key, "paged") == "strip" ? PageFlow.Strip : PageFlow.Paged;

    internal static void WriteFlow(IPluginConfig config, string key, PageFlow flow) =>
        config.Set(key, flow == PageFlow.Strip ? "strip" : "paged");

    private ViewerViewModel? _current;
    private ViewerView? _view;

    public bool IsOpen => _current is not null;

    public void Open(ViewerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ctx.Ui.CheckAccess())
        {
            ctx.Ui.Post(() => Open(request));
            return;
        }

        CloseCurrent();
        var settings = new ViewerSettings(ctx.Config.Get(BudgetKey, DefaultBudgetMb), ctx.Config.Get(StretchKey, false),
                                          ReadBookDefaults(ctx.Config), ReadFlow(ctx.Config, GalleryFlowKey));
        var vm = new ViewerViewModel(request, imaging, shell, settings, ctx.Log)
        {
            ImageWindows = windows,
            SaveGalleryFlow = flow => WriteFlow(ctx.Config, GalleryFlowKey, flow),
        };
        var view = new ViewerView(vm);
        vm.Closed += () =>
        {
            shell.CloseOverlay(view);
            if (ReferenceEquals(_current, vm))
            {
                _current = null;
                _view = null;
            }
        };
        _current = vm;
        _view = view;
        shell.ShowOverlay(ctx, view);
        vm.Start();
    }

    public void CloseCurrent()
    {
        var current = _current;
        _current = null;
        _view = null;
        if (current is not null)
        {
            _ = current.CloseAsync();
        }
    }

    public void RefreshTexts() => _current?.RefreshTexts();
}

/// <summary>Settings section "viewer".</summary>
internal sealed partial class ViewerSettingsViewModel : ObservableObject
{
    private readonly IPluginConfig _config;

    public ViewerSettingsViewModel(IPluginConfig config)
    {
        _config = config;
        var stored = config.Get(ImageViewerService.BudgetKey, ImageViewerService.DefaultBudgetMb);
        Budget = Budgets.MinBy(b => Math.Abs(b.Megabytes - stored))!;
        StretchSmall = config.Get(ImageViewerService.StretchKey, false);
        var book = ImageViewerService.ReadBookDefaults(config);
        Direction = Directions.First(d => d.Value == book.Direction);
        var layout = book.Flow == PageFlow.Strip ? BookLayout.Strip
            : book.Spread == SpreadMode.Single ? BookLayout.OnePage
            : BookLayout.TwoPages;
        Layout = Layouts.First(l => l.Value == layout);
        CoverAlone = book.CoverAlone;
        GalleryFlow = GalleryFlows.First(f => f.Value == ImageViewerService.ReadFlow(config, ImageViewerService.GalleryFlowKey));
    }

    /// <summary>"Pages shown" for books: spread and flow as one choice, like the viewer's button.</summary>
    public IReadOnlyList<Choice<BookLayout>> Layouts { get; } =
    [
        new(BookLayout.OnePage, "Viewer.Book.OnePage"),
        new(BookLayout.TwoPages, "Viewer.Book.TwoPages"),
        new(BookLayout.Strip, "Viewer.Flow.Strip"),
    ];

    public IReadOnlyList<Choice<PageFlow>> GalleryFlows { get; } =
        [new(PageFlow.Paged, "Viewer.Layout.OneImage"), new(PageFlow.Strip, "Viewer.Flow.Strip")];

    [ObservableProperty]
    public partial Choice<BookLayout> Layout { get; set; }

    [ObservableProperty]
    public partial Choice<PageFlow> GalleryFlow { get; set; }

    partial void OnGalleryFlowChanged(Choice<PageFlow> value) =>
        ImageViewerService.WriteFlow(_config, ImageViewerService.GalleryFlowKey, value.Value);

    partial void OnLayoutChanged(Choice<BookLayout> value)
    {
        // Strip keeps the stored spread, so switching back restores it.
        if (value.Value != BookLayout.Strip)
        {
            _config.Set(ImageViewerService.SpreadKey, value.Value == BookLayout.OnePage ? "single" : "double");
        }

        ImageViewerService.WriteFlow(_config, ImageViewerService.BookFlowKey,
                                     value.Value == BookLayout.Strip ? PageFlow.Strip : PageFlow.Paged);
    }

    public IReadOnlyList<BudgetOption> Budgets { get; } = [new(256), new(512), new(1024), new(2048), new(4096)];

    public IReadOnlyList<Choice<ReadingDirection>> Directions { get; } =
        [new(ReadingDirection.RightToLeft, "Viewer.Book.RightToLeft"), new(ReadingDirection.LeftToRight, "Viewer.Book.LeftToRight")];

    [ObservableProperty]
    public partial Choice<ReadingDirection> Direction { get; set; }

    [ObservableProperty]
    public partial bool CoverAlone { get; set; }

    partial void OnDirectionChanged(Choice<ReadingDirection> value) =>
        _config.Set(ImageViewerService.DirectionKey, value.Value == ReadingDirection.LeftToRight ? "ltr" : "rtl");


    partial void OnCoverAloneChanged(bool value) => _config.Set(ImageViewerService.CoverAloneKey, value);

    [ObservableProperty]
    public partial BudgetOption Budget { get; set; }

    [ObservableProperty]
    public partial bool StretchSmall { get; set; }

    partial void OnBudgetChanged(BudgetOption value) => _config.Set(ImageViewerService.BudgetKey, value.Megabytes);

    partial void OnStretchSmallChanged(bool value) => _config.Set(ImageViewerService.StretchKey, value);
}

/// <summary>What a book shows at a time.</summary>
internal enum BookLayout
{
    OnePage,
    TwoPages,
    Strip,
}

/// <summary>A cache budget choice.</summary>
/// <param name="Megabytes">Budget in MB.</param>
internal sealed record BudgetOption(int Megabytes)
{
    public string Label => Megabytes >= 1024
        ? Loc.Format("Viewer.Settings.GB", Megabytes / 1024)
        : Loc.Format("Viewer.Settings.MB", Megabytes);
}

/// <summary>A localized choice.</summary>
/// <typeparam name="T">Value type.</typeparam>
/// <param name="Value">The value.</param>
/// <param name="Key">Localization key of the label.</param>
internal sealed record Choice<T>(T Value, string Key)
{
    public string Label => Loc.Get(Key);
}

/// <summary>View of the "viewer" settings section.</summary>
internal sealed partial class ViewerSettingsView : UserControl
{
    public ViewerSettingsView() => InitializeComponent();
}
