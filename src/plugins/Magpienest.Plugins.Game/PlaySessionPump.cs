using Magpienest.Sdk;

namespace Magpienest.Plugins.Game;

/// <summary>
/// Calls <see cref="PlaySessionTracker.PollAsync"/> every <see cref="PlaySessionTracker.PollInterval"/> on a
/// background thread, but only while there is a session; nothing runs for a library nobody is playing from.
/// </summary>
internal sealed class PlaySessionPump(PlaySessionTracker tracker, ILog log, CancellationToken stopping)
{
    private int _running;

    /// <summary>Starts the loop when there are sessions and it is not running yet. Callable from any thread.</summary>
    public void Kick()
    {
        if (stopping.IsCancellationRequested || !tracker.HasSessions)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
        {
            _ = Task.Run(LoopAsync, CancellationToken.None);
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(PlaySessionTracker.PollInterval);
            while (tracker.HasSessions && await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                try
                {
                    await tracker.PollAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.Error("Following the running games failed.", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Deactivating.
        }
        finally
        {
            Volatile.Write(ref _running, 0);

            // A session may have started just after the loop decided to stop.
            Kick();
        }
    }
}
