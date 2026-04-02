using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace WebPhone.Registration;

/// <summary>
/// Allows to interact with an external channel, which is not owned by the current component, but can be used to send and receive messages through it.
/// </summary>
/// <typeparam name="TIncomingMessage"></typeparam>
public interface IBroadcastChannel<TIncomingMessage, TOutgoingMessage>
{
    ChannelWriter<TOutgoingMessage> Writer { get; }

    IChannelSubscription<TIncomingMessage> Subscribe();

    IChannelSubscription<TIncomingMessage> Subscribe(Func<TIncomingMessage, bool> filter);
}

public interface IChannelSubscription<T> : IDisposable
{
    ValueTask<T> ReadAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<T> ReadAllAsync(CancellationToken cancellationToken = default);
}

public class ChannelSubscription<T>(ChannelReader<T> reader, Action<ChannelSubscription<T>> onDisposed, Func<T, bool>? filter) : IChannelSubscription<T>
{
    public Func<T, bool>? Filter => filter;

    public async ValueTask<T> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out var item))
            {
                if (Filter is null || Filter(item))
                {
                    return item;
                }
            }
        }

        throw new ChannelClosedException();
    }

    public async IAsyncEnumerable<T> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in reader.ReadAllAsync(cancellationToken))
        {
            if (Filter is null || Filter(item))
            {
                yield return item;
            }
        }
    }

    public void Dispose()
        => onDisposed(this);
}

public interface IMessagesChannel : IBroadcastChannel<IncomingMessage, OutgoingMessage>
{

}

public static class ChannelExtensions
{
    public static async Task<TSpecificMessage?> WaitForSpecific<TCommonMessage, TSpecificMessage>(
        this IChannelSubscription<TCommonMessage> channelReader,
        Func<TCommonMessage, TSpecificMessage?> conversion,
        CancellationToken cancellationToken)
    {
        await foreach (var incomingMessage in channelReader
            .ReadAllAsync(cancellationToken)
            .WithCancellation(cancellationToken))
        {
            var candidate = conversion(incomingMessage);
            if (candidate is not null) { return candidate; }
        }

        return default;
    }
}