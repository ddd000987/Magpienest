using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Magpienest.Sdk;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Shell;

/// <summary><see cref="IShellService"/> implementation. UI thread unless stated otherwise.</summary>
internal sealed class ShellService : IShellService
{
    private const int MaxToasts = 4;

    private readonly IPluginContext _ctx;
    private readonly ShellViewModel _vm;
    private readonly MainWindow _window;
    private readonly List<PageDescriptor> _pages = [];
    private readonly Dictionary<PageDescriptor, NavItem> _navItems = [];
    private readonly Dictionary<PageDescriptor, FrameworkElement> _views = [];
    private readonly Stack<IInputElement?> _focusBeforeOverlay = new();
    private PageDescriptor? _current;
    private bool _started;
    private bool _closing;
    private bool _fullScreen;
    private bool _fullScreenByOverlay;
    private WindowState _stateBeforeFullScreen;
    private AppTheme _theme;

    public ShellService(IPluginContext ctx)
    {
        _ctx = ctx;
        _vm = new ShellViewModel(id => Navigate(id), collapsed => _ctx.Config.Set("railCollapsed", collapsed))
        {
            IsRailCollapsed = ctx.Config.Get("railCollapsed", false),
        };
        _window = new MainWindow { DataContext = _vm };
        _window.Closing += OnWindowClosing;
        RestoreBounds();

        _theme = ctx.Config.Get("theme", AppTheme.System);
        ApplyTheme(_theme);
        LocSource.Instance.PropertyChanged += OnLanguageChanged;
    }

    public Window MainWindow => _window;

    public IReadOnlyList<PageDescriptor> Pages => [.. SortedPages()];

    public string? CurrentPageId => _current?.Id;

    public bool IsFullScreen
    {
        get => _fullScreen;
        set
        {
            if (value == _fullScreen)
            {
                return;
            }

            _fullScreen = value;
            if (value)
            {
                // Pitfall 9: this exact order, or the taskbar stays visible.
                _stateBeforeFullScreen = _window.WindowState;
                _window.WindowState = WindowState.Normal;
                _window.WindowStyle = WindowStyle.None;
                _window.ResizeMode = ResizeMode.NoResize;
                _window.WindowState = WindowState.Maximized;
                _fullScreenByOverlay = _window.Overlays.Children.Count > 0;
            }
            else
            {
                _window.WindowState = WindowState.Normal;
                _window.WindowStyle = WindowStyle.SingleBorderWindow;
                _window.ResizeMode = ResizeMode.CanResize;
                _window.WindowState = _stateBeforeFullScreen;
                _fullScreenByOverlay = false;
            }

            _vm.IsFullScreen = value;
        }
    }

    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (value == _theme)
            {
                return;
            }

            _theme = value;
            _ctx.Config.Set("theme", value);
            ApplyTheme(value);
            _ctx.Emit(new ThemeChanged(value));
        }
    }

    /// <summary>Called on AppStarted: choose the first page and show the window.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        var remembered = _ctx.Config.Get<string?>("page", null);
        if (remembered is null || !Navigate(remembered))
        {
            NavigateToFirst(mainOnly: true);
        }

        // Automated smoke tests must not pop up a window; the window still exists for the host's check.
        if (!_ctx.Environment.CommandLineArgs.Contains("--smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            _window.Show();
        }
    }

    public void BringToFront()
    {
        if (!_window.IsVisible)
        {
            return;
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    /// <summary>Undo action of the plugin: save state and close the window.</summary>
    public void Close()
    {
        LocSource.Instance.PropertyChanged -= OnLanguageChanged;
        SaveBounds();
        _closing = true;
        _window.Close();
    }

    public void AddPage(IPluginContext owner, PageDescriptor page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (_pages.Any(p => p.Id == page.Id))
        {
            throw new InvalidOperationException($"A page with id '{page.Id}' is already registered.");
        }

        _pages.Add(page);
        _navItems[page] = new NavItem(page);
        RefreshNav();
        owner.Track(() => RemovePage(page));

        if (_started && _current is null && page.Placement == NavPlacement.Main)
        {
            Navigate(page.Id);
        }
    }

    public bool Navigate(string pageId, object? parameter = null)
    {
        var page = _pages.FirstOrDefault(p => p.Id == pageId);
        if (page is null)
        {
            return false;
        }

        if (page == _current)
        {
            if (parameter is not null && _views.TryGetValue(page, out var same))
            {
                NavigationAware(same)?.OnNavigatedTo(parameter);
            }

            _vm.Select(_navItems[page]);
            return true;
        }

        if (!_views.TryGetValue(page, out var view))
        {
            try
            {
                view = page.CreateView();
            }
            catch (Exception ex)
            {
                _ctx.Log.Error($"Creating the view of page '{page.Id}' failed.", ex);
                Notify(Loc.Get("Shell.Error.PageFailed"), NotifySeverity.Error);
                _vm.Select(_current is null ? null : _navItems[_current]);
                return false;
            }

            _views[page] = view;
        }

        if (_current is not null && _views.TryGetValue(_current, out var previous))
        {
            SafeCall(() => NavigationAware(previous)?.OnNavigatedFrom());
        }

        _current = page;
        _vm.PageContent = view;
        _vm.Select(_navItems[page]);
        _ctx.Config.Set("page", page.Id);
        SafeCall(() => NavigationAware(view)?.OnNavigatedTo(parameter));
        return true;
    }

    public void ShowOverlay(IPluginContext owner, FrameworkElement view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (_window.Overlays.Children.Contains(view))
        {
            return;
        }

        _focusBeforeOverlay.Push(Keyboard.FocusedElement);
        _window.Overlays.Children.Add(view);
        owner.Track(() => CloseOverlay(view));
        if (!view.Focusable)
        {
            view.Focusable = true;
        }

        view.Dispatcher.BeginInvoke(() => view.Focus(), DispatcherPriority.Input);
    }

    public void CloseOverlay(FrameworkElement view)
    {
        if (!_window.Overlays.Children.Contains(view))
        {
            return;
        }

        _window.Overlays.Children.Remove(view);
        var restore = _focusBeforeOverlay.Count > 0 ? _focusBeforeOverlay.Pop() : null;
        if (_window.Overlays.Children.Count == 0 && _fullScreen && _fullScreenByOverlay)
        {
            IsFullScreen = false;
        }

        if (restore is UIElement { IsVisible: true } element)
        {
            element.Focus();
        }
        else if (_vm.PageContent is UIElement page)
        {
            page.Focus();
        }
    }

    public void AddResources(IPluginContext owner, ResourceDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        var merged = Application.Current.Resources.MergedDictionaries;
        merged.Add(dictionary);
        owner.Track(() => merged.Remove(dictionary));
    }

    public void Notify(string message, NotifySeverity severity = NotifySeverity.Info)
    {
        if (!_ctx.Ui.CheckAccess())
        {
            _ctx.Ui.Post(() => Notify(message, severity));
            return;
        }

        if (_closing)
        {
            return;
        }

        var toast = new ToastItem(message, severity, t => _vm.Toasts.Remove(t));
        _vm.Toasts.Add(toast);
        while (_vm.Toasts.Count > MaxToasts)
        {
            _vm.Toasts.RemoveAt(0);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(severity == NotifySeverity.Error ? 8 : 4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _vm.Toasts.Remove(toast);
        };
        timer.Start();
    }

    public IBackgroundTask BeginTask(IPluginContext owner, string title)
    {
        var task = new BackgroundTaskItem(title, _ctx.Ui, t => _vm.Tasks.Remove(t));
        _vm.Tasks.Add(task);
        owner.Track(task.Dispose);
        return task;
    }

    public Task ShowMessageAsync(string title, string message) =>
        ShowDialogAsync(new DialogDescriptor { Title = title, Content = DialogWindow.MessageContent(message), CancelText = string.Empty });

    public Task<bool> ConfirmAsync(string title, string message, string? confirmText = null, string? cancelText = null) =>
        ShowDialogAsync(new DialogDescriptor
        {
            Title = title,
            Content = DialogWindow.MessageContent(message),
            PrimaryText = confirmText,
            CancelText = cancelText,
        });

    public Task<bool> ShowDialogAsync(DialogDescriptor dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        var result = new TaskCompletionSource<bool>();
        _ctx.Ui.Post(() =>
        {
            try
            {
                var window = new DialogWindow(dialog);
                if (_window.IsVisible)
                {
                    window.Owner = _window;
                    window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                }

                result.SetResult(window.ShowDialog() == true);
            }
            catch (Exception ex)
            {
                _ctx.Log.Error("Showing a dialog failed.", ex);
                result.TrySetResult(false);
            }
        });
        return result.Task;
    }

    private void RemovePage(PageDescriptor page)
    {
        var wasCurrent = page == _current;
        if (wasCurrent)
        {
            if (_views.TryGetValue(page, out var view))
            {
                SafeCall(() => NavigationAware(view)?.OnNavigatedFrom());
            }

            _current = null;
            _vm.PageContent = null;
        }

        _views.Remove(page);
        _navItems.Remove(page);
        _pages.Remove(page);
        RefreshNav();

        if (wasCurrent && _started && !_closing)
        {
            NavigateToFirst(mainOnly: false);
        }

        if (_current is null)
        {
            _vm.Select(null);
        }
    }

    private void NavigateToFirst(bool mainOnly)
    {
        var sorted = SortedPages().ToList();
        var first = sorted.FirstOrDefault(p => p.Placement == NavPlacement.Main)
            ?? (mainOnly ? null : sorted.FirstOrDefault());
        if (first is not null)
        {
            Navigate(first.Id);
        }
        else
        {
            _vm.Select(null);
        }
    }

    private IEnumerable<PageDescriptor> SortedPages() =>
        _pages.OrderBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal);

    private void RefreshNav()
    {
        var sorted = SortedPages().ToList();
        Sync(_vm.MainItems, sorted.Where(p => p.Placement == NavPlacement.Main));
        Sync(_vm.FooterItems, sorted.Where(p => p.Placement == NavPlacement.Footer));
        _vm.Select(_current is null ? null : _navItems.GetValueOrDefault(_current));

        void Sync(System.Collections.ObjectModel.ObservableCollection<NavItem> target, IEnumerable<PageDescriptor> pages)
        {
            var wanted = pages.Select(p => _navItems[p]).ToList();
            for (var i = target.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(target[i]))
                {
                    target.RemoveAt(i);
                }
            }

            for (var i = 0; i < wanted.Count; i++)
            {
                var index = target.IndexOf(wanted[i]);
                if (index < 0)
                {
                    target.Insert(i, wanted[i]);
                }
                else if (index != i)
                {
                    target.Move(index, i);
                }
            }
        }
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_ctx.Ui.CheckAccess())
        {
            _vm.RefreshTexts();
        }
        else
        {
            _ctx.Ui.Post(_vm.RefreshTexts);
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        // The user closed the window: keep it until the kernel shuts down, which closes it through Close().
        e.Cancel = true;
        SaveBounds();
        _ctx.Environment.RequestShutdown();
    }

    private void RestoreBounds()
    {
        var saved = _ctx.Config.Get<WindowBounds?>("window", null);
        if (saved is null || saved.Width < _window.MinWidth || saved.Height < _window.MinHeight)
        {
            return;
        }

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);
        if (!screen.IntersectsWith(bounds))
        {
            return;
        }

        _window.WindowStartupLocation = WindowStartupLocation.Manual;
        _window.Left = saved.Left;
        _window.Top = saved.Top;
        _window.Width = saved.Width;
        _window.Height = saved.Height;
        if (saved.Maximized)
        {
            _window.WindowState = WindowState.Maximized;
        }
    }

    private void SaveBounds()
    {
        if (!_window.IsLoaded)
        {
            return;
        }

        var maximized = _window.WindowState == WindowState.Maximized || (_fullScreen && _stateBeforeFullScreen == WindowState.Maximized);
        var rect = _window.WindowState == WindowState.Normal && !_fullScreen
            ? new Rect(_window.Left, _window.Top, _window.ActualWidth, _window.ActualHeight)
            : _window.RestoreBounds;
        if (rect.IsEmpty || double.IsNaN(rect.Width))
        {
            return;
        }

        _ctx.Config.Set("window", new WindowBounds { Left = rect.Left, Top = rect.Top, Width = rect.Width, Height = rect.Height, Maximized = maximized });
    }

    private void SafeCall(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _ctx.Log.Error("A page navigation callback threw.", ex);
        }
    }

    private static INavigationAware? NavigationAware(FrameworkElement view) =>
        view as INavigationAware ?? view.DataContext as INavigationAware;

    private static void ApplyTheme(AppTheme theme)
    {
        Application.Current.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    /// <summary>Persisted window rectangle.</summary>
    internal sealed class WindowBounds
    {
        public double Left { get; set; }

        public double Top { get; set; }

        public double Width { get; set; }

        public double Height { get; set; }

        public bool Maximized { get; set; }
    }
}
