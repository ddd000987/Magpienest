using System.Windows;

namespace Magpienest.Sdk.Shell;

/// <summary>Provided by the Shell plugin: main window, navigation, overlays, theme and feedback.</summary>
public interface IShellService
{
    /// <summary>The main window.</summary>
    Window MainWindow { get; }

    /// <summary>Adds a navigation page; removed when <paramref name="owner"/> deactivates.</summary>
    /// <param name="owner">Registering plugin.</param>
    /// <param name="page">The page.</param>
    void AddPage(IPluginContext owner, PageDescriptor page);

    /// <summary>
    /// Shows or hides a small dot on a page's navigation entry ("something new here"). The dot is shown while at
    /// least one plugin has it on, also for a page that is added later, and is cleared when <paramref name="owner"/>
    /// deactivates.
    /// </summary>
    /// <param name="owner">Plugin that sets the dot.</param>
    /// <param name="pageId">Page id.</param>
    /// <param name="visible">True to show it.</param>
    void SetBadge(IPluginContext owner, string pageId, bool visible);

    /// <summary>Registered pages.</summary>
    IReadOnlyList<PageDescriptor> Pages { get; }

    /// <summary>Id of the page shown, or null.</summary>
    string? CurrentPageId { get; }

    /// <summary>Shows a page. Returns false when the id is unknown.</summary>
    /// <param name="pageId">Page id.</param>
    /// <param name="parameter">Passed to <see cref="INavigationAware.OnNavigatedTo"/>.</param>
    bool Navigate(string pageId, object? parameter = null);

    /// <summary>Shows a full-window layer above navigation; removed if <paramref name="owner"/> deactivates.</summary>
    /// <param name="owner">Registering plugin.</param>
    /// <param name="view">The overlay view.</param>
    void ShowOverlay(IPluginContext owner, FrameworkElement view);

    /// <summary>Removes an overlay.</summary>
    /// <param name="view">The overlay view.</param>
    void CloseOverlay(FrameworkElement view);

    /// <summary>Whether the main window is fullscreen.</summary>
    bool IsFullScreen { get; set; }

    /// <summary>Theme; persisted by Shell and applied immediately.</summary>
    AppTheme Theme { get; set; }

    /// <summary>Merges a dictionary into the application resources; removed when <paramref name="owner"/> deactivates.</summary>
    /// <param name="owner">Registering plugin.</param>
    /// <param name="dictionary">The dictionary.</param>
    void AddResources(IPluginContext owner, ResourceDictionary dictionary);

    /// <summary>Shows a toast. Callable from any thread.</summary>
    /// <param name="message">Localized text.</param>
    /// <param name="severity">Severity.</param>
    void Notify(string message, NotifySeverity severity = NotifySeverity.Info);

    /// <summary>Adds a status-bar entry; dispose the result when finished.</summary>
    /// <param name="owner">Registering plugin.</param>
    /// <param name="title">Localized title.</param>
    IBackgroundTask BeginTask(IPluginContext owner, string title);

    /// <summary>Shows a message dialog.</summary>
    /// <param name="title">Localized title.</param>
    /// <param name="message">Localized message.</param>
    Task ShowMessageAsync(string title, string message);

    /// <summary>Shows a confirmation dialog; true when confirmed.</summary>
    /// <param name="title">Localized title.</param>
    /// <param name="message">Localized message.</param>
    /// <param name="confirmText">Localized confirm text; null = "OK".</param>
    /// <param name="cancelText">Localized cancel text; null = "Cancel".</param>
    Task<bool> ConfirmAsync(string title, string message, string? confirmText = null, string? cancelText = null);

    /// <summary>Shows a modal dialog; true when the primary button was chosen.</summary>
    /// <param name="dialog">The dialog.</param>
    Task<bool> ShowDialogAsync(DialogDescriptor dialog);
}

/// <summary>A navigation page.</summary>
public sealed record PageDescriptor
{
    /// <summary>Page id, e.g. "illustration".</summary>
    public required string Id { get; init; }

    /// <summary>Localization key of the title.</summary>
    public required string TitleKey { get; init; }

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public required string Glyph { get; init; }

    /// <summary>Ascending sort order.</summary>
    public int Order { get; init; }

    /// <summary>Where the page appears in the navigation rail.</summary>
    public NavPlacement Placement { get; init; } = NavPlacement.Main;

    /// <summary>Called once, lazily; the view is kept until the page is removed.</summary>
    public required Func<FrameworkElement> CreateView { get; init; }
}

/// <summary>Navigation rail group.</summary>
public enum NavPlacement
{
    /// <summary>Top group.</summary>
    Main,

    /// <summary>Bottom group.</summary>
    Footer,
}

/// <summary>Optional; implemented by a page view or its DataContext.</summary>
public interface INavigationAware
{
    /// <summary>The page became current.</summary>
    /// <param name="parameter">Navigation parameter.</param>
    void OnNavigatedTo(object? parameter);

    /// <summary>The page stopped being current.</summary>
    void OnNavigatedFrom();
}

/// <summary>A modal dialog.</summary>
public sealed record DialogDescriptor
{
    /// <summary>Localized title.</summary>
    public required string Title { get; init; }

    /// <summary>Dialog content.</summary>
    public required FrameworkElement Content { get; init; }

    /// <summary>Localized primary text; null = "OK".</summary>
    public string? PrimaryText { get; init; }

    /// <summary>Localized cancel text; null = "Cancel"; "" = no cancel button.</summary>
    public string? CancelText { get; init; }

    /// <summary>Polled to enable the primary button.</summary>
    public Func<bool>? CanAccept { get; init; }

    /// <summary>Dialog width in DIPs.</summary>
    public double Width { get; init; } = 480;
}

/// <summary>A status-bar entry. Dispose = finished; callable from any thread.</summary>
public interface IBackgroundTask : IDisposable
{
    /// <summary>Reports progress.</summary>
    /// <param name="fraction">0..1, or null for indeterminate.</param>
    /// <param name="status">Localized status text.</param>
    void Report(double? fraction, string? status = null);
}

/// <summary>Application theme.</summary>
public enum AppTheme
{
    /// <summary>Follow Windows.</summary>
    System,

    /// <summary>Light.</summary>
    Light,

    /// <summary>Dark.</summary>
    Dark,
}

/// <summary>Toast severity.</summary>
public enum NotifySeverity
{
    /// <summary>Information.</summary>
    Info,

    /// <summary>Success.</summary>
    Success,

    /// <summary>Warning.</summary>
    Warning,

    /// <summary>Error.</summary>
    Error,
}

/// <summary>Kernel-bus event: the theme changed.</summary>
/// <param name="Theme">The new theme.</param>
public sealed record ThemeChanged(AppTheme Theme);

/// <summary>Kernel-bus event emitted by the app when a second instance starts.</summary>
public sealed record AppActivationRequested;
