using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using Magpienest.Sdk;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.WebUi.Tests;

/// <summary>T7.6: the "Web" page follows its setting through a removable scope.</summary>
public sealed class WebPageTests
{
    [Fact]
    public void ScopeUndoesNewestFirstOnceAndKeepsGoingAfterAFailure()
    {
        var context = new FakeContext();
        var scope = new PageScope(context);
        List<int> order = [];
        scope.Track(() => order.Add(1));
        scope.Track(() => throw new InvalidOperationException("boom"));
        scope.Track(new Undo(() => order.Add(3)));

        scope.Dispose();
        scope.Dispose();

        Assert.Equal([3, 1], order);
        Assert.Empty(context.Tracked); // Nothing leaked into the plugin's own context.
    }

    [Fact]
    public void ScopeUndoesAtOnceAfterItWasClosed()
    {
        var scope = new PageScope(new FakeContext());
        scope.Dispose();
        var undone = false;

        scope.Track(() => undone = true);

        Assert.True(undone);
    }

    [Fact]
    public void ScopePassesEverythingElseToThePlugin()
    {
        var context = new FakeContext();
        var scope = new PageScope(context);

        Assert.Same(context.Config, scope.Config);
        Assert.Same(context.Log, scope.Log);
        Assert.Equal(context.DataDirectory, scope.DataDirectory);
        Assert.Equal("svc", scope.Get<string>());
    }

    [Fact]
    public void PageIsAddedAndRemovedWithTheSetting()
    {
        var shell = new PageShell();
        using var host = new WebPageHost(new FakeContext(), shell, () => throw new InvalidOperationException("not shown"));

        host.Apply(true);
        host.Apply(true);
        var page = Assert.Single(shell.Pages);
        Assert.Equal(WebPageHost.PageId, page.Id);
        Assert.Equal(500, page.Order);
        Assert.Equal(NavPlacement.Main, page.Placement);
        Assert.Equal("WebUi.Page.Title", page.TitleKey);

        host.Apply(false);
        Assert.Empty(shell.Pages);
        Assert.False(host.IsShown);

        host.Apply(true);
        Assert.Single(shell.Pages);
        host.Dispose(); // Deactivation.
        Assert.Empty(shell.Pages);
    }

    [Fact]
    public void RemovingThePageClosesItsView()
    {
        RunSta(() =>
        {
            var shell = new PageShell();
            ClosingView? view = null;
            var host = new WebPageHost(new FakeContext(), shell, () => view = new ClosingView());
            host.Apply(true);

            Assert.IsType<ClosingView>(shell.Pages[0].CreateView());
            host.Apply(false);

            Assert.True(view!.Closed);
        });
    }

    [Fact]
    public void ShowPageDefaultsOnAndIsStored()
    {
        var config = new MemoryConfig();
        var settings = new WebUiSettings(config);
        Assert.True(settings.ShowPage);

        settings.ShowPage = false;

        Assert.False(new WebUiSettings(config).ShowPage);
    }

    [Fact]
    public async Task StoppedServerShowsTheMessageAndTrustsNothing()
    {
        var context = new FakeContext();
        var settings = new WebUiSettings(context.Config);
        await using var controller = new WebUiController(settings, new NullBackend(), new WebEventHub(), new NullLog(), "", null);
        using var model = new WebPageViewModel(context, controller, new PageShell());

        Assert.Null(model.Address);
        Assert.True(model.NotRunning);
        Assert.True(model.ShowMessage);
        Assert.False(model.ShowBrowser);
        Assert.False(model.IsOwnAddress("http://127.0.0.1:47290/"));
        Assert.False(model.AcceptsCertificate("https://127.0.0.1:47290/", "AB"));
        Assert.EndsWith(Path.Combine("cache", "webview2"), model.UserDataFolder);

        model.RuntimeFailed(new InvalidOperationException("no runtime"));
        Assert.True(model.NoRuntime);
        Assert.False(model.NotRunning);
        Assert.True(model.ShowMessage);
    }

    [Fact]
    public async Task RunningServerIsTheOnlyAddressThePageStaysOn()
    {
        var context = new FakeContext();
        var settings = new WebUiSettings(context.Config) { Port = FreePort() };
        await using var controller = new WebUiController(settings, new NullBackend(), new WebEventHub(), new NullLog(),
                                                         Path.GetTempPath(), null);
        Assert.Equal(WebServerState.Running, await controller.StartAsync());
        using var model = new WebPageViewModel(context, controller, new PageShell());

        Assert.Equal(new Uri(controller.EntryLink), model.Address);
        Assert.Equal("127.0.0.1", model.Address!.Host);
        Assert.True(model.ShowBrowser);
        Assert.True(model.IsOwnAddress(controller.BaseAddress + "#/comic"));
        Assert.False(model.IsOwnAddress($"http://localhost:{controller.Port}/"));
        Assert.False(model.IsOwnAddress($"http://127.0.0.1:{controller.Port + 1}/"));
        Assert.False(model.IsOwnAddress("https://example.com/"));
        Assert.False(model.IsOwnAddress("not a url"));

        // HTTP: there is no own certificate, so no certificate error is ever ignored.
        Assert.Null(controller.CertificateThumbprint);
        Assert.False(model.AcceptsCertificate(controller.BaseAddress, "AB"));
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new Xunit.Sdk.XunitException("STA test failed: " + error);
        }
    }

    private sealed class Undo(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    private sealed class ClosingView : Border, IDisposable
    {
        public bool Closed { get; private set; }

        public void Dispose() => Closed = true;
    }

    /// <summary>A plugin context whose tracked effects are only recorded.</summary>
    private sealed class FakeContext : IPluginContext
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mn-webpage-" + Guid.NewGuid().ToString("N"));

        public List<Action> Tracked { get; } = [];

        public PluginManifest Manifest => throw new NotSupportedException();

        public string PluginDirectory => _root;

        public string DataDirectory => Path.Combine(_root, "data");

        public IAppEnvironment Environment => new FakeEnvironment(_root);

        public ILog Log { get; } = new NullLog();

        public IPluginConfig Config { get; } = new MemoryConfig();

        public IUiDispatcher Ui { get; } = new InlineUi();

        public CancellationToken Deactivating => CancellationToken.None;

        public T Get<T>() where T : class => typeof(T) == typeof(string) ? (T)(object)"svc" : throw new NotSupportedException();

        public bool TryGet<T>([NotNullWhen(true)] out T? service) where T : class
        {
            service = null;
            return false;
        }

        public void Provide<T>(T implementation) where T : class => throw new NotSupportedException();

        public void On<TEvent>(Action<TEvent> handler) => throw new NotSupportedException();

        public void Emit<TEvent>(TEvent evt) => throw new NotSupportedException();

        public void Track(IDisposable effect) => Tracked.Add(effect.Dispose);

        public void Track(Action undo) => Tracked.Add(undo);
    }

    private sealed class FakeEnvironment(string root) : IAppEnvironment
    {
        public Version AppVersion => new(1, 0);

        public string AppDirectory => root;

        public string DataDirectory => root;

        public string CacheDirectory => Path.Combine(root, "cache");

        public IReadOnlyList<string> CommandLineArgs => [];

        public bool IsStarted => true;

        public void RequestShutdown(int exitCode = 0) => throw new NotSupportedException();
    }

    private sealed class NullBackend : IWebUiBackend
    {
        public Task<WebState> GetStateAsync() => Task.FromResult(new WebState("en", "light", []));

        public Task<IReadOnlyDictionary<string, string>> GetStringsAsync() =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }

    private sealed class InlineUi : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public void Post(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
    }

    /// <summary>Keeps pages like the Shell plugin does: removed through the owner's tracking.</summary>
    private sealed class PageShell : IShellService
    {
        private readonly List<PageDescriptor> _pages = [];

        public IReadOnlyList<PageDescriptor> Pages => _pages;

        public Window MainWindow => throw new NotSupportedException();

        public string? CurrentPageId => null;

        public void SetBadge(IPluginContext owner, string pageId, bool visible) { }

        public bool IsFullScreen { get; set; }

        public AppTheme Theme { get; set; }

        public void AddPage(IPluginContext owner, PageDescriptor page)
        {
            _pages.Add(page);
            owner.Track(() => _pages.Remove(page));
        }

        public bool Navigate(string pageId, object? parameter = null) => false;

        public void ShowOverlay(IPluginContext owner, FrameworkElement view) => throw new NotSupportedException();

        public void CloseOverlay(FrameworkElement view) => throw new NotSupportedException();

        public void AddResources(IPluginContext owner, ResourceDictionary dictionary) => throw new NotSupportedException();

        public void Notify(string message, NotifySeverity severity = NotifySeverity.Info)
        {
        }

        public IBackgroundTask BeginTask(IPluginContext owner, string title) => throw new NotSupportedException();

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message, string? confirmText = null, string? cancelText = null) =>
            Task.FromResult(false);

        public Task<bool> ShowDialogAsync(DialogDescriptor dialog) => Task.FromResult(false);
    }
}
