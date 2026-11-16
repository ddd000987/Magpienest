using GenHTTP.Api.Protocol;

namespace Magpienest.Plugins.WebUi;

/// <summary>A response body held in memory.</summary>
internal sealed class BytesContent(byte[] bytes) : IResponseContent
{
    public ulong? Length => (ulong)bytes.Length;

    public ContentType? Type => null;

    public ReadOnlyMemory<byte>? Encoding => null;

    public ValueTask<ulong?> CalculateChecksumAsync() => new((ulong?)null);

    public async ValueTask WriteAsync(IResponseSink sink) => await sink.Stream.WriteAsync(bytes);
}

/// <summary>A byte range of a file, read in chunks while it is sent.</summary>
internal sealed class FileContent(string path, long start, long count) : IResponseContent
{
    private const int BufferSize = 81920;

    public ulong? Length => (ulong)count;

    public ContentType? Type => null;

    public ReadOnlyMemory<byte>? Encoding => null;

    public ValueTask<ulong?> CalculateChecksumAsync() => new((ulong?)null);

    public async ValueTask WriteAsync(IResponseSink sink)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                                                BufferSize, useAsync: true);
        stream.Position = start;
        var buffer = new byte[BufferSize];
        var left = count;
        while (left > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)));
            if (read == 0)
            {
                break;
            }

            await sink.Stream.WriteAsync(buffer.AsMemory(0, read));
            left -= read;
        }
    }
}

/// <summary>A Server-Sent Events stream: one subscription to the hub, kept open until the client or the server leaves.</summary>
internal sealed class EventStreamContent(WebEventHub hub, CancellationToken serverStopping) : IResponseContent
{
    /// <summary>A comment line is sent this often so dead connections are noticed and proxies keep the stream.</summary>
    internal static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    public ulong? Length => null;

    public ContentType? Type => null;

    public ReadOnlyMemory<byte>? Encoding => null;

    public ValueTask<ulong?> CalculateChecksumAsync() => new((ulong?)null);

    public async ValueTask WriteAsync(IResponseSink sink)
    {
        using var subscription = hub.Subscribe();
        var stream = sink.Stream;
        try
        {
            await WriteAsync(stream, ": connected\n\n");
            while (!serverStopping.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(serverStopping);
                wait.CancelAfter(Heartbeat);
                string message;
                try
                {
                    message = await subscription.Reader.ReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!serverStopping.IsCancellationRequested)
                {
                    message = ": ping\n\n";
                }

                await WriteAsync(stream, message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away or the server is stopping.
        }
    }

    private static async Task WriteAsync(Stream stream, string text)
    {
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));
        await stream.FlushAsync();
    }
}
