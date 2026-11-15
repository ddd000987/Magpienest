using Magpienest.Sdk;

namespace Magpienest.Plugins.Library.Browser;

/// <summary>
/// Runs the last scheduled action after a quiet period. Every <see cref="Schedule"/> cancels the pending one.
/// Must be used from one thread (the UI thread in the app).
/// </summary>
internal sealed class Debouncer(TimeSpan delay, Func<TimeSpan, CancellationToken, Task> wait, ILog? log = null) : IDisposable
{
    private CancellationTokenSource? _pending;
    private bool _disposed;

    /// <summary>Completes when the most recently scheduled action has run (or was superseded).</summary>
    public Task Last { get; private set; } = Task.CompletedTask;

    public void Schedule(Func<Task> action)
    {
        if (_disposed)
        {
            return;
        }

        _pending?.Cancel();
        _pending?.Dispose();
        _pending = new CancellationTokenSource();
        Last = RunAsync(action, _pending.Token);
    }

    public void Cancel()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }

    private async Task RunAsync(Func<Task> action, CancellationToken token)
    {
        try
        {
            await wait(delay, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            log?.Error("A debounced action failed.", ex);
        }
    }
}
