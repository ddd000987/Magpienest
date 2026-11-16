using System.Text.Json;
using System.Threading.Channels;

namespace Magpienest.Plugins.WebUi;

/// <summary>Fans server events (language, theme, library) out to every open <c>/api/events</c> stream. Thread-safe.</summary>
internal sealed class WebEventHub
{
    /// <summary>Messages a slow client may fall behind by before the oldest are dropped.</summary>
    private const int Backlog = 64;

    private readonly Lock _gate = new();
    private readonly List<Channel<string>> _clients = [];

    /// <summary>Number of open streams.</summary>
    public int ClientCount
    {
        get
        {
            lock (_gate)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>Formats one SSE message.</summary>
    /// <param name="name">Event name.</param>
    /// <param name="data">Object serialized as camelCase JSON.</param>
    public static string Format(string name, object data) =>
        $"event: {name}\ndata: {JsonSerializer.Serialize(data, WebJson.Options)}\n\n";

    /// <summary>Sends an event to every open stream.</summary>
    /// <param name="name">Event name.</param>
    /// <param name="data">Object serialized as camelCase JSON.</param>
    public void Publish(string name, object data)
    {
        var message = Format(name, data);
        lock (_gate)
        {
            foreach (var client in _clients)
            {
                client.Writer.TryWrite(message);
            }
        }
    }

    /// <summary>Opens a stream; dispose the subscription when the client leaves.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(Backlog)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_gate)
        {
            _clients.Add(channel);
        }

        return new Subscription(this, channel);
    }

    private void Remove(Channel<string> channel)
    {
        lock (_gate)
        {
            _clients.Remove(channel);
        }

        channel.Writer.TryComplete();
    }

    /// <summary>One open stream.</summary>
    internal sealed class Subscription(WebEventHub hub, Channel<string> channel) : IDisposable
    {
        /// <summary>Messages for this stream.</summary>
        public ChannelReader<string> Reader => channel.Reader;

        public void Dispose() => hub.Remove(channel);
    }
}
