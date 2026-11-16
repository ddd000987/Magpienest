using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpienest.Plugins.Library.Browser;
using Magpienest.Sdk;
using Magpienest.Sdk.Library;
using Magpienest.Sdk.Shell;
using Magpienest.Sdk.Thumbnails;

namespace Magpienest.Plugins.Library.Tests;

internal sealed class NullLog : ILog
{
    public void Debug(string message) { }

    public void Info(string message) { }

    public void Warn(string message, Exception? ex = null) { }

    public void Error(string message, Exception? ex = null) { }
}

internal sealed class MemoryConfig : IPluginConfig
{
    public Dictionary<string, object?> Values { get; } = [];

    public T Get<T>(string key, T defaultValue) => Values.TryGetValue(key, out var value) && value is T typed ? typed : defaultValue;

    public void Set<T>(string key, T value) => Values[key] = value;
}

internal sealed class NullThumbnails : IThumbnailService
{
    public void RegisterProvider(IPluginContext owner, string kind, IThumbnailProvider provider) { }

    public Task<ImageSource?> GetAsync(LibraryItem item, CancellationToken ct = default) => Task.FromResult<ImageSource?>(null);

    public Task<byte[]?> GetJpegAsync(LibraryItem item, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

    public Task InvalidateAsync(LibraryItem item) => Task.CompletedTask;

    public Task ClearCacheAsync() => Task.CompletedTask;

    public Task<BitmapSource?> GetShellImageAsync(string path, int pixelSize, bool iconOnly, CancellationToken ct = default) =>
        Task.FromResult<BitmapSource?>(null);
}

/// <summary>Records toasts; dialogs return <see cref="ConfirmResult"/>; the rest is unsupported.</summary>
internal sealed class FakeShell : IShellService
{
    public List<string> Toasts { get; } = [];

    public bool ConfirmResult { get; set; } = true;

    public Window MainWindow => throw new NotSupportedException();

    public IReadOnlyList<PageDescriptor> Pages => [];

    public string? CurrentPageId => null;

    public void SetBadge(IPluginContext owner, string pageId, bool visible) { }

    public bool IsFullScreen { get; set; }

    public AppTheme Theme { get; set; }

    public void AddPage(IPluginContext owner, PageDescriptor page) => throw new NotSupportedException();

    public bool Navigate(string pageId, object? parameter = null) => false;

    public void ShowOverlay(IPluginContext owner, FrameworkElement view) => throw new NotSupportedException();

    public void CloseOverlay(FrameworkElement view) => throw new NotSupportedException();

    public void AddResources(IPluginContext owner, ResourceDictionary dictionary) => throw new NotSupportedException();

    public void Notify(string message, NotifySeverity severity = NotifySeverity.Info) => Toasts.Add(message);

    public IBackgroundTask BeginTask(IPluginContext owner, string title) => new NullTask();

    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message, string? confirmText = null, string? cancelText = null) =>
        Task.FromResult(ConfirmResult);

    public Task<bool> ShowDialogAsync(DialogDescriptor dialog) => Task.FromResult(false);

    private sealed class NullTask : IBackgroundTask
    {
        public void Report(double? fraction, string? status = null) { }

        public void Dispose() { }
    }
}

/// <summary>Debounce waits the test releases by hand.</summary>
internal sealed class ManualDelay
{
    private readonly object _lock = new();
    private readonly List<TaskCompletionSource> _gates = [];

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _gates.Count;
            }
        }
    }

    public Task Wait(TimeSpan delay, CancellationToken ct)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => gate.TrySetCanceled(ct));
        lock (_lock)
        {
            _gates.Add(gate);
        }

        return gate.Task;
    }

    /// <summary>Releases every gate that is still waiting.</summary>
    public void ReleaseAll()
    {
        lock (_lock)
        {
            foreach (var gate in _gates)
            {
                gate.TrySetResult();
            }
        }
    }
}

internal static class BrowserHarness
{
    public static BrowserServices Services(ILibraryService library, Func<TimeSpan, CancellationToken, Task>? delay = null, FakeShell? shell = null) => new()
    {
        Library = library,
        Thumbnails = new NullThumbnails(),
        Shell = shell ?? new FakeShell(),
        Config = new MemoryConfig(),
        Log = new NullLog(),
        Delay = delay ?? ((_, _) => Task.CompletedTask),
        PickFolder = () => null,
    };

    public static BrowserOptions Options(string kind = "test", bool showStatus = false) => new()
    {
        Kind = kind,
        OpenItem = _ => Task.CompletedTask,
        ShowStatus = showStatus,
        Sorts = [ItemSort.Title, ItemSort.AddedAt, ItemSort.Rating],
    };
}
