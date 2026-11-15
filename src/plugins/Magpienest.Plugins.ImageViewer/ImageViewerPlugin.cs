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
public sealed class ImageViewerPlugin : Plugin
{
    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var service = new ImageViewerService(ctx, ctx.Get<IShellService>(), ctx.Get<IImagingService>());
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
internal sealed class ImageViewerService(IPluginContext ctx, IShellService shell, IImagingService imaging) : IImageViewerService
{
    internal const string BudgetKey = "cacheBudgetMb";
    internal const string StretchKey = "stretchSmall";
    internal const int DefaultBudgetMb = 512;

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
        var settings = new ViewerSettings(ctx.Config.Get(BudgetKey, DefaultBudgetMb), ctx.Config.Get(StretchKey, false));
        var vm = new ViewerViewModel(request, imaging, shell, settings, ctx.Log);
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
    }

    public IReadOnlyList<BudgetOption> Budgets { get; } = [new(256), new(512), new(1024), new(2048), new(4096)];

    [ObservableProperty]
    public partial BudgetOption Budget { get; set; }

    [ObservableProperty]
    public partial bool StretchSmall { get; set; }

    partial void OnBudgetChanged(BudgetOption value) => _config.Set(ImageViewerService.BudgetKey, value.Megabytes);

    partial void OnStretchSmallChanged(bool value) => _config.Set(ImageViewerService.StretchKey, value);
}

/// <summary>A cache budget choice.</summary>
/// <param name="Megabytes">Budget in MB.</param>
internal sealed record BudgetOption(int Megabytes)
{
    public string Label => Megabytes >= 1024
        ? Loc.Format("Viewer.Settings.GB", Megabytes / 1024)
        : Loc.Format("Viewer.Settings.MB", Megabytes);
}

/// <summary>View of the "viewer" settings section.</summary>
internal sealed partial class ViewerSettingsView : UserControl
{
    public ViewerSettingsView() => InitializeComponent();
}
