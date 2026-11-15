using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.About;

/// <summary>Footer page with version, licence, third-party notices and the data folder.</summary>
[Requires(typeof(IShellService))]
public sealed class AboutPlugin : Plugin
{
    /// <inheritdoc />
    public override void Activate(IPluginContext ctx)
    {
        var shell = ctx.Get<IShellService>();
        shell.AddPage(ctx, new PageDescriptor
        {
            Id = "about",
            TitleKey = "About.Nav.Title",
            Glyph = "",
            Order = 900,
            Placement = NavPlacement.Footer,
            CreateView = () => new AboutView { DataContext = new AboutViewModel(ctx) },
        });
    }
}

/// <summary>State of the About page.</summary>
internal sealed partial class AboutViewModel : ObservableObject
{
    private readonly IPluginContext _ctx;

    public AboutViewModel(IPluginContext ctx)
    {
        _ctx = ctx;
        _ = LoadNoticesAsync();
    }

    public string Version => _ctx.Environment.AppVersion.ToString(3);

    public string DataDirectory => _ctx.Environment.DataDirectory;

    [ObservableProperty]
    public partial string Notices { get; set; } = string.Empty;

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(_ctx.Environment.DataDirectory);

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(Path.Combine(_ctx.Environment.DataDirectory, "logs"));

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ctx.Log.Warn($"Could not open '{path}'.", ex);
        }
    }

    private async Task LoadNoticesAsync()
    {
        var path = Path.Combine(_ctx.PluginDirectory, "THIRD-PARTY-NOTICES.md");
        try
        {
            var text = await File.ReadAllTextAsync(path, _ctx.Deactivating).ConfigureAwait(true);
            Notices = text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _ctx.Log.Warn($"Could not read '{path}'.", ex);
        }
    }
}
