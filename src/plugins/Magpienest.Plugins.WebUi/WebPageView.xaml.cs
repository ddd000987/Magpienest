using System.ComponentModel;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Magpienest.Plugins.WebUi;

/// <summary>
/// The "Web" page: a WebView2 control showing this PC's own link. The control starts the first time there is something
/// to show; if its runtime is missing, the page explains that and offers the browser instead.
/// </summary>
internal sealed partial class WebPageView : UserControl, IDisposable
{
    private readonly WebPageViewModel _model;
    private WebView2? _browser;
    private Uri? _shown;
    private bool _starting;
    private bool _disposed;

    public WebPageView(WebPageViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        model.PropertyChanged += OnModelChanged;
        Loaded += (_, _) => _ = ShowAsync();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _model.PropertyChanged -= OnModelChanged;
        _model.Dispose();
        BrowserHost.Children.Clear();
        _browser?.Dispose();
        _browser = null;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WebPageViewModel.Address))
        {
            _ = ShowAsync();
        }
    }

    /// <summary>Starts the control if needed and shows the current address.</summary>
    private async Task ShowAsync()
    {
        if (_disposed || !IsLoaded || !_model.ShowBrowser || _model.Address is not { } address)
        {
            return;
        }

        if (_browser is { CoreWebView2: { } core })
        {
            // Only a new address reloads; coming back to the page keeps where the user was.
            if (address != _shown)
            {
                _shown = address;
                core.Navigate(address.AbsoluteUri);
            }

            return;
        }

        if (_starting)
        {
            return; // The start in progress shows the latest address when it is done.
        }

        _starting = true;
        var browser = new WebView2();
        try
        {
            BrowserHost.Children.Add(browser);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _model.UserDataFolder);
            await browser.EnsureCoreWebView2Async(environment);
            if (_disposed)
            {
                browser.Dispose();
                return;
            }

            var started = browser.CoreWebView2;
            started.Settings.IsStatusBarEnabled = false;
            started.ServerCertificateErrorDetected += OnCertificateError;
            started.NavigationStarting += OnNavigationStarting;
            started.NewWindowRequested += OnNewWindowRequested;
            _browser = browser;
        }
        catch (Exception ex)
        {
            // WebView2RuntimeNotFoundException, or a runtime that failed to start (COMException and others).
            BrowserHost.Children.Remove(browser);
            browser.Dispose();
            if (!_disposed)
            {
                _model.RuntimeFailed(ex);
            }

            return;
        }
        finally
        {
            _starting = false;
        }

        await ShowAsync();
    }

    /// <summary>Trusts exactly the certificate this plugin's server presents; every other error stays an error.</summary>
    private void OnCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        using var certificate = e.ServerCertificate.ToX509Certificate2();
        e.Action = _model.AcceptsCertificate(e.RequestUri, certificate.Thumbprint)
            ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
            : CoreWebView2ServerCertificateErrorAction.Cancel;
    }

    /// <summary>The page stays on this PC's own server; other links open in the default browser.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!_model.IsOwnAddress(e.Uri))
        {
            e.Cancel = true;
            _model.OpenExternal(e.Uri);
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (_model.IsOwnAddress(e.Uri))
        {
            _browser?.CoreWebView2?.Navigate(e.Uri);
        }
        else
        {
            _model.OpenExternal(e.Uri);
        }
    }
}
