using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.WebUi;

/// <summary>Adds the "Web" page to the navigation, or removes it again, following the setting. UI thread only.</summary>
internal sealed class WebPageHost(IPluginContext ctx, IShellService shell, Func<object> createView) : IDisposable
{
    /// <summary>Page id.</summary>
    public const string PageId = "webui";

    private PageScope? _scope;

    /// <summary>Whether the page is in the navigation.</summary>
    public bool IsShown => _scope is not null;

    /// <summary>Shows or removes the page.</summary>
    /// <param name="show">The setting.</param>
    public void Apply(bool show)
    {
        if (show == IsShown)
        {
            return;
        }

        if (!show)
        {
            var old = _scope!;
            _scope = null;
            old.Dispose(); // Closes the view (and its browser) and removes the page.
            return;
        }

        var scope = new PageScope(ctx);
        _scope = scope;
        shell.AddPage(scope, new PageDescriptor
        {
            Id = PageId,
            TitleKey = "WebUi.Page.Title",
            Glyph = "", // Segoe Fluent Icons: World
            Order = 500,
            CreateView = () =>
            {
                var view = createView();
                if (view is IDisposable disposable)
                {
                    scope.Track(disposable);
                }

                return (System.Windows.FrameworkElement)view;
            },
        });
    }

    public void Dispose() => Apply(false);
}

/// <summary>
/// State of the "Web" page: the address to show (always this PC's own link with the token), whether the browser
/// control could start, and which certificate it may trust.
/// </summary>
internal sealed partial class WebPageViewModel : ObservableObject, INavigationAware, IDisposable
{
    private readonly IPluginContext _ctx;
    private readonly WebUiController _controller;
    private readonly IShellService _shell;

    public WebPageViewModel(IPluginContext ctx, WebUiController controller, IShellService shell)
    {
        _ctx = ctx;
        _controller = controller;
        _shell = shell;
        _controller.StateChanged += OnStateChanged;
        Update();
    }

    /// <summary>The token link while the server runs; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRunning), nameof(ShowBrowser), nameof(ShowMessage))]
    private Uri? _address;

    /// <summary>The WebView2 runtime is missing or failed to start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotRunning), nameof(ShowBrowser), nameof(ShowMessage))]
    private bool _noRuntime;

    /// <summary>The browser control can start but the server is not listening.</summary>
    public bool NotRunning => !NoRuntime && Address is null;

    /// <summary>Whether the browser control is shown.</summary>
    public bool ShowBrowser => !NoRuntime && Address is not null;

    /// <summary>Whether the explanation is shown instead.</summary>
    public bool ShowMessage => !ShowBrowser;

    /// <summary>The browser profile folder (<c>&lt;data&gt;/cache/webview2</c>).</summary>
    public string UserDataFolder => Path.Combine(_ctx.Environment.CacheDirectory, "webview2");

    /// <summary>Whether a certificate error for <paramref name="requestUri"/> may be ignored: only this PC's own server
    /// presenting exactly the certificate it was started with.</summary>
    /// <param name="requestUri">The address that failed.</param>
    /// <param name="thumbprint">The certificate's SHA-1 thumbprint.</param>
    public bool AcceptsCertificate(string requestUri, string thumbprint) =>
        Uri.TryCreate(requestUri, UriKind.Absolute, out var uri)
        && Address is { } address
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host == address.Host
        && uri.Port == address.Port
        && _controller.CertificateThumbprint is { } own
        && string.Equals(own, thumbprint, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a navigation stays on this PC's own server; anything else opens in the browser.</summary>
    /// <param name="requestUri">The address.</param>
    public bool IsOwnAddress(string requestUri) =>
        Uri.TryCreate(requestUri, UriKind.Absolute, out var uri)
        && Address is { } address
        && uri.Scheme == address.Scheme
        && uri.Host == address.Host
        && uri.Port == address.Port;

    /// <summary>The browser control could not start.</summary>
    /// <param name="error">What failed.</param>
    public void RuntimeFailed(Exception error)
    {
        _ctx.Log.Warn("Web interface: the WebView2 runtime could not start; the Web page shows the browser button instead.", error);
        NoRuntime = true;
    }

    /// <summary>Opens a link of this page in the default browser.</summary>
    /// <param name="url">An http or https address.</param>
    public void OpenExternal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Launch(uri.AbsoluteUri);
        }
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (_controller.State == WebServerState.Running)
        {
            Launch(_controller.EntryLink);
        }
        else
        {
            _shell.Notify(Loc.Get("WebUi.Page.NotRunning"), NotifySeverity.Warning);
        }
    }

    /// <summary>A reset access link takes effect when the page is shown again.</summary>
    /// <param name="parameter">Unused.</param>
    public void OnNavigatedTo(object? parameter) => Update();

    public void OnNavigatedFrom()
    {
    }

    public void Dispose() => _controller.StateChanged -= OnStateChanged;

    private void Launch(string link) => _ = Task.Run(() =>
    {
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _ctx.Log.Warn("Web interface: could not open the browser.", ex);
            _ctx.Ui.Post(() => _shell.Notify(Loc.Get("WebUi.Error.OpenBrowser"), NotifySeverity.Error));
        }
    });

    private void OnStateChanged() => _ctx.Ui.Post(Update);

    private void Update()
    {
        var address = _controller.State == WebServerState.Running ? new Uri(_controller.EntryLink) : null;
        if (address != Address)
        {
            Address = address;
        }
    }
}
