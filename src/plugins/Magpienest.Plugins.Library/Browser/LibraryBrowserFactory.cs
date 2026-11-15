using System.Windows;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>Creates browsers and forwards <see cref="LibraryChanged"/> to the live ones.</summary>
internal sealed class LibraryBrowserFactory(BrowserServices services) : ILibraryBrowserFactory
{
    private readonly List<BrowserViewModel> _live = [];

    public FrameworkElement CreateBrowser(IPluginContext owner, BrowserOptions options)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(options);
        var vm = new BrowserViewModel(services, owner, options);
        _live.Add(vm);
        owner.Track(() =>
        {
            _live.Remove(vm);
            vm.Dispose();
        });
        return new BrowserView(vm);
    }

    /// <summary>Kernel-bus handler (UI thread).</summary>
    public void OnLibraryChanged(LibraryChanged change)
    {
        foreach (var vm in _live.ToArray())
        {
            vm.OnLibraryChanged(change);
        }
    }
}
