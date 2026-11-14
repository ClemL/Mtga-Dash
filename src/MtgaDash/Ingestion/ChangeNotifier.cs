using System.Threading.Channels;

namespace MtgaDash.Ingestion;

/// <summary>Fans a "data changed" signal out to every open dashboard (SSE) and to the static exporter.</summary>
public sealed class ChangeNotifier
{
    private readonly Lock _gate = new();
    private readonly List<Channel<string>> _subscribers = [];

    public event Action? Changed;

    public DateTime LastChangedUtc { get; private set; } = DateTime.UtcNow;

    public ChannelReader<string> Subscribe(CancellationToken cancel)
    {
        // Bounded and dropping: a stalled browser tab must not hold memory; one pending "changed" is enough.
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _subscribers.Add(channel);
        cancel.Register(() =>
        {
            lock (_gate) _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        });
        return channel.Reader;
    }

    public void Notify()
    {
        LastChangedUtc = DateTime.UtcNow;
        var stamp = Data.Time.Format(LastChangedUtc);
        lock (_gate)
            foreach (var s in _subscribers) s.Writer.TryWrite(stamp);
        Changed?.Invoke();
    }
}
