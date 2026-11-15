using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpienest.Sdk;
using Magpienest.Sdk.Localization;
using Magpienest.Sdk.Shell;

namespace Magpienest.Plugins.Shell;

/// <summary>State of the main window.</summary>
internal sealed partial class ShellViewModel : ObservableObject
{
    private readonly Action<string> _navigate;
    private readonly Action<bool> _railChanged;
    private bool _syncing;

    public ShellViewModel(Action<string> navigate, Action<bool> railChanged)
    {
        _navigate = navigate;
        _railChanged = railChanged;
        Tasks.CollectionChanged += (_, _) => OnTasksChanged();
    }

    public ObservableCollection<NavItem> MainItems { get; } = [];

    public ObservableCollection<NavItem> FooterItems { get; } = [];

    public ObservableCollection<ToastItem> Toasts { get; } = [];

    public ObservableCollection<BackgroundTaskItem> Tasks { get; } = [];

    [ObservableProperty]
    public partial bool IsRailCollapsed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial object? PageContent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusBar))]
    public partial bool IsFullScreen { get; set; }

    [ObservableProperty]
    public partial NavItem? SelectedMain { get; set; }

    [ObservableProperty]
    public partial NavItem? SelectedFooter { get; set; }

    public bool IsEmpty => PageContent is null;

    public BackgroundTaskItem? CurrentTask => Tasks.Count > 0 ? Tasks[^1] : null;

    public bool ShowStatusBar => Tasks.Count > 0 && !IsFullScreen;

    public string MoreText => Tasks.Count > 1 ? Loc.Format("Shell.Status.More", Tasks.Count - 1) : string.Empty;

    public void Select(NavItem? item)
    {
        _syncing = true;
        try
        {
            SelectedMain = item is not null && MainItems.Contains(item) ? item : null;
            SelectedFooter = item is not null && FooterItems.Contains(item) ? item : null;
        }
        finally
        {
            _syncing = false;
        }
    }

    public void RefreshTexts()
    {
        foreach (var item in MainItems.Concat(FooterItems))
        {
            item.RefreshTitle();
        }

        OnPropertyChanged(nameof(MoreText));
    }

    [RelayCommand]
    private void ToggleRail() => IsRailCollapsed = !IsRailCollapsed;

    partial void OnIsRailCollapsedChanged(bool value) => _railChanged(value);

    partial void OnSelectedMainChanged(NavItem? value)
    {
        if (!_syncing && value is not null)
        {
            _navigate(value.Page.Id);
        }
    }

    partial void OnSelectedFooterChanged(NavItem? value)
    {
        if (!_syncing && value is not null)
        {
            _navigate(value.Page.Id);
        }
    }

    private void OnTasksChanged()
    {
        OnPropertyChanged(nameof(CurrentTask));
        OnPropertyChanged(nameof(ShowStatusBar));
        OnPropertyChanged(nameof(MoreText));
    }
}

/// <summary>One entry in the navigation rail.</summary>
internal sealed class NavItem(PageDescriptor page) : ObservableObject
{
    public PageDescriptor Page { get; } = page;

    public string Glyph => Page.Glyph;

    /// <summary>Read from the localization source every time; never cached.</summary>
    public string Title => Loc.Get(Page.TitleKey);

    public void RefreshTitle() => OnPropertyChanged(nameof(Title));
}

/// <summary>A toast card.</summary>
internal sealed partial class ToastItem(string message, NotifySeverity severity, Action<ToastItem> close) : ObservableObject
{
    public string Message { get; } = message;

    public NotifySeverity Severity { get; } = severity;

    [RelayCommand]
    private void Close() => close(this);
}

/// <summary>A status-bar entry. Report and Dispose are callable from any thread.</summary>
internal sealed partial class BackgroundTaskItem : ObservableObject, IBackgroundTask
{
    private readonly IUiDispatcher _ui;
    private readonly Action<BackgroundTaskItem> _remove;
    private int _disposed;

    public BackgroundTaskItem(string title, IUiDispatcher ui, Action<BackgroundTaskItem> remove)
    {
        Title = title;
        _ui = ui;
        _remove = remove;
    }

    public string Title { get; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; set; } = true;

    [ObservableProperty]
    public partial string? Status { get; set; }

    public void Report(double? fraction, string? status = null)
    {
        if (!_ui.CheckAccess())
        {
            _ui.Post(() => Report(fraction, status));
            return;
        }

        IsIndeterminate = fraction is null;
        Progress = Math.Clamp(fraction ?? 0, 0, 1);
        Status = status;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (_ui.CheckAccess())
            {
                _remove(this);
            }
            else
            {
                _ui.Post(() => _remove(this));
            }
        }
    }
}
