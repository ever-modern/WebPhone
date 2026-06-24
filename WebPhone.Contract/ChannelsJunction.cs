using System.Threading.Channels;
using EverModern.Threading.Channels;
using EverModern.Threading.Locks;

namespace WebPhone.Domain;

public class ChannelsJunction<TIn, TOut> : IBroadcastChannel<TIn, TOut>, IDisposable
{
    readonly CancellationTokenSource _cts = new();
    readonly Lock _subscriptionLocker = new();

    readonly List<Channel<TIn>> _subscribers = [];

    readonly ChannelOptions? _subscriptionOptions;

    volatile bool _disposed;

    public ChannelWriter<TOut> Writer { get; }

    ChannelsJunction(ChannelReader<TIn> reader,
        ChannelWriter<TOut> writer,
        ChannelOptions? subscriptionOptions = null)
    {
        Writer = writer;
        _subscriptionOptions = subscriptionOptions;
    }

    public IChannelSubscription<TIn> Subscribe(Func<TIn, bool> filter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Channel<TIn> channel = _subscriptionOptions switch
        {
            BoundedChannelOptions bounded => Channel.CreateBounded<TIn>(bounded),
            UnboundedChannelOptions unbounded => Channel.CreateUnbounded<TIn>(unbounded),
            _ => Channel.CreateBounded<TIn>(50)
        };

        using var _ = _subscriptionLocker.LockScope();

        _subscribers.Add(channel);

        return new ChannelSubscription<TIn>(
            channel.Reader,
            (_) =>
            {
                using var __ = _subscriptionLocker.LockScope();
                _subscribers.Remove(channel);
            },
            filter
        );
    }

    public static async Task<ChannelsJunction<TIn, TOut>> CreateAsync(
        ChannelReader<TIn> reader,
        ChannelWriter<TOut> writer,
        ChannelOptions? subscriptionOptions = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ChannelsJunction<TIn, TOut>(reader, writer, subscriptionOptions);
        var totalCts = CancellationTokenSource.CreateLinkedTokenSource(result._cts.Token, cancellationToken);
        await BroadcastLoop(
            reader,
            result._subscribers,
            result._subscriptionLocker,
            totalCts.Token
        );

        return result;
    }

    static async Task BroadcastLoop(ChannelReader<TIn> reader, List<Channel<TIn>> subscribers, Lock subLocker, CancellationToken cancellationToken)
    {
        TaskCompletionSource startedTcs = new();
        bool started = false;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var message in reader.ReadAllAsync(cancellationToken).Prepend(default!))
                    {
                        using var lockHolder = subLocker.LockScope();

                        if (started is false)
                        {
                            startedTcs.TrySetResult();
                            started = true;
                            continue;
                        }

                        var subSnapshot = subscribers.ToArray();

                        lockHolder.Finish();

                        foreach (var subscriber in subSnapshot)
                        {
                            try
                            {
                                await subscriber.Writer.WriteAsync(message, cancellationToken);
                            }
                            catch (ChannelClosedException) {}
                        }
                    }
                }
                catch (OperationCanceledException) {}
                finally
                {
                    foreach (var subscriber in subscribers)
                    {
                        subscriber.Writer.TryComplete();
                    }

                    subscribers.Clear();
                }
            },
            cancellationToken
        );

        await startedTcs.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
    }
}
