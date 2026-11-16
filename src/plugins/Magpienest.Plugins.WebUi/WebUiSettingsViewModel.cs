using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;
using QRCoder;

namespace Magpienest.Plugins.WebUi;

/// <summary>One link for other devices, with its QR code.</summary>
/// <param name="Url">The address.</param>
/// <param name="Code">QR code image of the address.</param>
internal sealed record DeviceLink(string Url, BitmapSource Code);

/// <summary>One signed-in device in the list.</summary>
/// <param name="Id">Session id.</param>
/// <param name="Name">Browser and system.</param>
/// <param name="Detail">Last seen and address.</param>
internal sealed record SignedInDevice(string Id, string Name, string Detail);

/// <summary>The "Web interface" settings section.</summary>
internal sealed partial class WebUiSettingsViewModel : ObservableObject
{
    private readonly IPluginContext _ctx;
    private readonly WebUiController _controller;
    private readonly IShellService _shell;
    private readonly WebUiSettings _settings;
    private readonly WebPageHost? _page;
    private bool _busy;

    public WebUiSettingsViewModel(IPluginContext ctx, WebUiController controller, IShellService shell, WebPageHost? page = null)
    {
        _page = page;
        _ctx = ctx;
        _controller = controller;
        _shell = shell;
        _settings = controller.Settings;
        _portText = controller.Port > 0 ? controller.Port.ToString(CultureInfo.InvariantCulture) : "";
        _extraHostsText = string.Join(", ", _settings.ExtraHosts);
        Refresh();
    }

    /// <summary>Starts following the server, the sessions and the language (the view calls this when it is shown).</summary>
    public void Attach()
    {
        _controller.StateChanged += OnStateChanged;
        if (_controller.Sessions is { } sessions)
        {
            sessions.Changed += OnStateChanged;
        }

        LocSource.Instance.PropertyChanged += OnLanguageChanged;
        Refresh();
    }

    /// <summary>Stops following (the view calls this when it is closed).</summary>
    public void Detach()
    {
        _controller.StateChanged -= OnStateChanged;
        if (_controller.Sessions is { } sessions)
        {
            sessions.Changed -= OnStateChanged;
        }

        LocSource.Instance.PropertyChanged -= OnLanguageChanged;
    }

    // ---- Status and this PC ----

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _hasProblem;

    /// <summary>Whether the "Web" page is in the navigation; applied at once.</summary>
    public bool ShowPage
    {
        get => _settings.ShowPage;
        set
        {
            _settings.ShowPage = value;
            _page?.Apply(value);
            OnPropertyChanged();
        }
    }

    // ---- Who can connect ----

    public bool IsLocal
    {
        get => _settings.Access == AccessMode.Local;
        set => SelectMode(AccessMode.Local, value);
    }

    public bool IsHome
    {
        get => _settings.Access == AccessMode.Home;
        set => SelectMode(AccessMode.Home, value);
    }

    public bool IsInternet
    {
        get => _settings.Access == AccessMode.Internet;
        set => SelectMode(AccessMode.Internet, value);
    }

    /// <summary>Whether other devices may connect (shows the links, firewall note and password options).</summary>
    public bool IsShared => _settings.Access != AccessMode.Local;

    public bool RequirePasswordAtHome
    {
        get => _settings.RequirePasswordAtHome;
        set => _ = SetRequirePasswordAsync(value);
    }

    public bool TailscaleIsHome
    {
        get => _settings.TailscaleIsHome;
        set
        {
            _settings.TailscaleIsHome = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    [ObservableProperty]
    private string _extraHostsText;

    partial void OnExtraHostsTextChanged(string value) => _settings.ExtraHosts = WebUiSettings.ParseHosts(value);

    /// <summary>Whether some device must sign in: the password card is shown only then.</summary>
    public bool PasswordInUse =>
        _settings.Access == AccessMode.Internet || (_settings.Access == AccessMode.Home && _settings.RequirePasswordAtHome);

    /// <summary>"Set password…" or "Change password…".</summary>
    public string PasswordButtonText => Loc.Get(_settings.HasPassword ? "WebUi.Settings.ChangePassword" : "WebUi.Settings.SetPassword");

    /// <summary>Whether a password exists (status line under the button).</summary>
    public string PasswordStatus => Loc.Get(_settings.HasPassword ? "WebUi.Settings.PasswordSet" : "WebUi.Settings.PasswordNotSet");

    /// <summary>Links for phones and other devices.</summary>
    public ObservableCollection<DeviceLink> Links { get; } = [];

    /// <summary>Whether no home-network address was found.</summary>
    [ObservableProperty]
    private bool _noLinks;

    /// <summary>The router hint in internet mode.</summary>
    [ObservableProperty]
    private string _internetHint = "";

    // ---- HTTPS ----

    public bool HttpsOff
    {
        get => _settings.Https == HttpsMode.Off;
        set => SelectHttps(HttpsMode.Off, value);
    }

    public bool HttpsAutomatic
    {
        get => _settings.Https == HttpsMode.Automatic;
        set => SelectHttps(HttpsMode.Automatic, value);
    }

    public bool HttpsOwn
    {
        get => _settings.Https == HttpsMode.Own;
        set => SelectHttps(HttpsMode.Own, value);
    }

    /// <summary>"HTTPS is always on for internet access" while HTTPS is set to off in internet mode.</summary>
    public bool HttpsForced => _settings.Access == AccessMode.Internet && _settings.Https == HttpsMode.Off;

    /// <summary>The chosen certificate file's name.</summary>
    public string OwnCertificateName => _settings.OwnCertificatePath is { } path ? Path.GetFileName(path) : "";

    /// <summary>Whether the automatic certificate is in use.</summary>
    public bool UsesAutomaticCertificate => _settings.EffectiveHttps == HttpsMode.Automatic;

    // ---- Devices ----

    public ObservableCollection<SignedInDevice> Devices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoDevices))]
    private bool _hasDevices;

    /// <summary>Whether the "no devices" text shows.</summary>
    public bool NoDevices => !HasDevices;

    // ---- Port ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortError))]
    [NotifyCanExecuteChangedFor(nameof(ApplyPortCommand))]
    private string _portText;

    /// <summary>Whether the port box can be edited (not when <c>--webui-port</c> is used).</summary>
    public bool CanEditPort => !_controller.PortOverridden;

    /// <summary>Error under the port box, or empty.</summary>
    public string PortError => WebUiSettings.ParsePort(PortText) is null ? Loc.Get("WebUi.Settings.PortInvalid") : "";

    private bool CanApplyPort() => CanEditPort && WebUiSettings.ParsePort(PortText) is { } port &&
                                   (port != _controller.Port || _controller.State != WebServerState.Running);

    [RelayCommand(CanExecute = nameof(CanApplyPort))]
    private async Task ApplyPortAsync()
    {
        if (WebUiSettings.ParsePort(PortText) is { } port)
        {
            _settings.Port = port;
            await RestartAsync();
        }
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        var link = _controller.EntryLink;
        _ = Task.Run(() =>
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
    }

    [RelayCommand]
    private void CopyLink() => Copy(_controller.EntryLink);

    [RelayCommand]
    private void CopyAddress(string? url)
    {
        if (!string.IsNullOrEmpty(url))
        {
            Copy(url);
        }
    }

    [RelayCommand]
    private async Task ResetLinkAsync()
    {
        if (await _shell.ConfirmAsync(Loc.Get("WebUi.Settings.ResetConfirmTitle"), Loc.Get("WebUi.Settings.ResetConfirmText"),
                                      Loc.Get("WebUi.Settings.ResetLink")))
        {
            _controller.ResetToken();
            _shell.Notify(Loc.Get("WebUi.Settings.ResetDone"), NotifySeverity.Success);
        }
    }

    [RelayCommand]
    private async Task SetPasswordAsync() => await AskNewPasswordAsync();

    [RelayCommand]
    private async Task ChooseCertificateAsync()
    {
        if (await ChooseOwnCertificateAsync())
        {
            _settings.Https = HttpsMode.Own;
            RaiseHttps();
            await RestartAsync();
        }
    }

    [RelayCommand]
    private async Task RenewCertificateAsync()
    {
        if (await _shell.ConfirmAsync(Loc.Get("WebUi.Settings.RenewTitle"), Loc.Get("WebUi.Settings.RenewText"),
                                      Loc.Get("WebUi.Settings.Renew")))
        {
            StatusText = Loc.Get("WebUi.Settings.Status.Starting");
            await Report(await Task.Run(_controller.RenewCertificateAsync));
        }
    }

    [RelayCommand]
    private void SignOutDevice(string? id)
    {
        if (id is not null)
        {
            _controller.Sessions?.Remove(id);
        }
    }

    [RelayCommand]
    private async Task SignOutAllAsync()
    {
        if (await _shell.ConfirmAsync(Loc.Get("WebUi.Settings.SignOutAllTitle"), Loc.Get("WebUi.Settings.SignOutAllText"),
                                      Loc.Get("WebUi.Settings.SignOutAll")))
        {
            _controller.Sessions?.Clear();
        }
    }

    // ---- Mode and HTTPS changes ----

    private void SelectMode(AccessMode mode, bool selected)
    {
        if (selected && mode != _settings.Access && !_busy)
        {
            _ = ChangeModeAsync(mode);
        }
    }

    private async Task ChangeModeAsync(AccessMode mode)
    {
        _busy = true;
        try
        {
            var needsPassword = mode == AccessMode.Internet || (mode == AccessMode.Home && _settings.RequirePasswordAtHome);
            if (needsPassword && !_settings.HasPassword && !await AskNewPasswordAsync())
            {
                return;
            }

            if (mode == AccessMode.Internet &&
                !await _shell.ConfirmAsync(Loc.Get("WebUi.Settings.InternetTitle"),
                                           Loc.Format("WebUi.Settings.InternetText", _settings.Port),
                                           Loc.Get("WebUi.Settings.InternetConfirm")))
            {
                return;
            }

            _settings.Access = mode;
            await RestartAsync();
        }
        finally
        {
            _busy = false;
            RaiseMode();
        }
    }

    private async Task SetRequirePasswordAsync(bool value)
    {
        if (value && !_settings.HasPassword && !await AskNewPasswordAsync())
        {
            OnPropertyChanged(nameof(RequirePasswordAtHome));
            return;
        }

        // Turning the password off opens the library to everyone on the Wi-Fi: say so once and let the user back out.
        if (!value && _settings.RequirePasswordAtHome &&
            !await _shell.ConfirmAsync(Loc.Get("WebUi.Settings.PasswordOffTitle"), Loc.Get("WebUi.Settings.PasswordOffText"),
                                       Loc.Get("WebUi.Settings.PasswordOffConfirm")))
        {
            OnPropertyChanged(nameof(RequirePasswordAtHome));
            return;
        }

        // Read for every request: no restart needed.
        _settings.RequirePasswordAtHome = value;
        OnPropertyChanged(nameof(RequirePasswordAtHome));
        OnPropertyChanged(nameof(PasswordInUse));
    }

    private void SelectHttps(HttpsMode mode, bool selected)
    {
        if (selected && mode != _settings.Https && !_busy)
        {
            _ = ChangeHttpsAsync(mode);
        }
    }

    private async Task ChangeHttpsAsync(HttpsMode mode)
    {
        _busy = true;
        try
        {
            if (mode == HttpsMode.Own && !await ChooseOwnCertificateAsync())
            {
                return;
            }

            _settings.Https = mode;
            await RestartAsync();
        }
        finally
        {
            _busy = false;
            RaiseHttps();
        }
    }

    private async Task<bool> AskNewPasswordAsync()
    {
        var content = new PasswordDialog("WebUi.Password.Hint", isNewPassword: true);
        if (!await _shell.ShowDialogAsync(new DialogDescriptor
            {
                Title = Loc.Get(_settings.HasPassword ? "WebUi.Settings.ChangePassword" : "WebUi.Settings.SetPassword"),
                Content = content,
                PrimaryText = Loc.Get("WebUi.Password.Save"),
                CanAccept = () => content.IsValid,
            }))
        {
            return false;
        }

        var password = content.Password;
        await Task.Run(() => _controller.SetPassword(password));
        _shell.Notify(Loc.Get("WebUi.Settings.PasswordSaved"), NotifySeverity.Success);
        OnPropertyChanged(nameof(PasswordButtonText));
        OnPropertyChanged(nameof(PasswordStatus));
        return true;
    }

    private async Task<bool> ChooseOwnCertificateAsync()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("WebUi.Settings.ChooseCertificate"),
            Filter = Loc.Get("WebUi.Settings.CertificateFilter") + " (*.pfx;*.p12)|*.pfx;*.p12",
            CheckFileExists = true,
        };
        if (picker.ShowDialog(_shell.MainWindow) != true)
        {
            return false;
        }

        var path = picker.FileName;
        var content = new PasswordDialog("WebUi.Password.CertificateHint", isNewPassword: false);
        if (!await _shell.ShowDialogAsync(new DialogDescriptor
            {
                Title = Loc.Get("WebUi.Settings.ChooseCertificate"),
                Content = content,
                PrimaryText = Loc.Get("WebUi.Password.Use"),
                CanAccept = () => content.IsValid,
            }))
        {
            return false;
        }

        var password = content.Password;
        using var certificate = await Task.Run(() => CertificateStore.TryLoad(path, password));
        if (certificate is null)
        {
            _shell.Notify(Loc.Get("WebUi.Error.Certificate"), NotifySeverity.Error);
            return false;
        }

        _settings.OwnCertificatePath = path;
        _settings.OwnCertificatePassword = CertificateStore.Protect(password);
        OnPropertyChanged(nameof(OwnCertificateName));
        return true;
    }

    private async Task RestartAsync()
    {
        StatusText = Loc.Get("WebUi.Settings.Status.Starting");
        await Report(await Task.Run(_controller.StartAsync));
    }

    private Task Report(WebServerState state)
    {
        switch (state)
        {
            case WebServerState.PortInUse:
                _shell.Notify(Loc.Format("WebUi.Error.PortInUse", _controller.Port), NotifySeverity.Error);
                break;
            case WebServerState.CertificateProblem:
                _shell.Notify(Loc.Get("WebUi.Error.Certificate"), NotifySeverity.Error);
                break;
        }

        Refresh();
        return Task.CompletedTask;
    }

    // ---- Refresh ----

    private void OnStateChanged() => _ctx.Ui.Post(Refresh);

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => _ctx.Ui.Post(Refresh);

    private void RaiseMode()
    {
        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(IsHome));
        OnPropertyChanged(nameof(IsInternet));
        OnPropertyChanged(nameof(IsShared));
        OnPropertyChanged(nameof(PasswordInUse));
        RaiseHttps();
        Refresh();
    }

    private void RaiseHttps()
    {
        OnPropertyChanged(nameof(HttpsOff));
        OnPropertyChanged(nameof(HttpsAutomatic));
        OnPropertyChanged(nameof(HttpsOwn));
        OnPropertyChanged(nameof(HttpsForced));
        OnPropertyChanged(nameof(UsesAutomaticCertificate));
        OnPropertyChanged(nameof(OwnCertificateName));
    }

    private void Refresh()
    {
        IsRunning = _controller.State == WebServerState.Running;
        HasProblem = _controller.State is WebServerState.PortInUse or WebServerState.CertificateProblem;
        StatusText = _controller.State switch
        {
            WebServerState.Running => Loc.Format("WebUi.Settings.Status.Running", _controller.BaseAddress),
            WebServerState.PortInUse => Loc.Format("WebUi.Settings.Status.PortInUse", _controller.Port),
            WebServerState.CertificateProblem => Loc.Get("WebUi.Settings.Status.Certificate"),
            _ => Loc.Get("WebUi.Settings.Status.Stopped"),
        };
        if (_controller.Port > 0 && string.IsNullOrEmpty(PortText))
        {
            PortText = _controller.Port.ToString(CultureInfo.InvariantCulture);
        }

        RefreshLinks();
        RefreshDevices();
        OnPropertyChanged(nameof(PortError));
        OnPropertyChanged(nameof(PasswordButtonText));
        OnPropertyChanged(nameof(PasswordStatus));
        ApplyPortCommand.NotifyCanExecuteChanged();
    }

    private void RefreshLinks()
    {
        Links.Clear();
        var shared = IsRunning && _controller.Mode != AccessMode.Local;
        var addresses = shared ? NetworkLinks.HomeAddresses(_settings.TailscaleIsHome) : [];
        foreach (var address in addresses)
        {
            var url = _controller.Address(address.ToString());
            Links.Add(new DeviceLink(url, QrCode(url)));
        }

        NoLinks = shared && Links.Count == 0;
        InternetHint = _controller.Mode == AccessMode.Internet && addresses.Count > 0
            ? Loc.Format("WebUi.Settings.InternetHint", _controller.Port, addresses[0])
            : "";
    }

    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var session in _controller.Sessions?.List() ?? [])
        {
            Devices.Add(new SignedInDevice(session.Id, session.Device,
                Loc.Format("WebUi.Settings.DeviceDetail", session.LastSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                           session.LastAddress)));
        }

        HasDevices = Devices.Count > 0;
    }

    private void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            _shell.Notify(Loc.Get("WebUi.Settings.LinkCopied"), NotifySeverity.Success);
        }
        catch (COMException ex)
        {
            _ctx.Log.Warn("Web interface: clipboard is busy.", ex);
            _shell.Notify(Loc.Get("WebUi.Error.Clipboard"), NotifySeverity.Error);
        }
    }

    /// <summary>A QR code of a link (black on white so every phone camera reads it).</summary>
    private static BitmapSource QrCode(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
