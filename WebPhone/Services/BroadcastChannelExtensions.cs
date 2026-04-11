using EverModern.Events;
using EverModern.Threading.Channels;
using System.Diagnostics;

namespace WebPhone.Services;

public static class BroadcastChannelExtensions
{
    public static Subscription ProcessIncoming<TIncoming, TOutgoing>(
        this IBroadcastChannel<TIncoming, TOutgoing> channel,
        Action<TIncoming> action
    )
    {
        var cancellationTokenSource = new CancellationTokenSource();
        Task.Run(async () =>
        {
            using var reader = channel.Subscribe();
            await foreach (var message in reader.ReadAllAsync(cancellationTokenSource.Token))
            {
                action(message);
                if (cancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }
            }
        });
        var result = new Subscription(() => cancellationTokenSource.Cancel());
        return result;
    }

    public static CancellationToken WhileReceiving<TIncoming, TOutgoing>(
        this IBroadcastChannel<TIncoming, TOutgoing> channel,
        Func<TIncoming, bool> filter,
        TimeSpan timeout,
        CancellationToken cancellationToken
    ) 
    {
        var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lastReceived = Stopwatch.GetTimestamp();
        Task.Run(async () =>
        {
            using var reader = channel.Subscribe(filter);            
            await foreach (var message in reader.ReadAllAsync(cancellationTokenSource.Token))
            {
                lastReceived = Stopwatch.GetTimestamp();
            }
        });

        var timer = new PeriodicTimer(TimeSpan.FromTicks(timeout.Ticks / 2));
        Task.Run(async () =>
        {
            while (await timer.WaitForNextTickAsync(cancellationTokenSource.Token))
            {
                var elapsed = TimeSpan.FromTicks(Stopwatch.GetTimestamp() - lastReceived);
                if (elapsed > timeout)
                {
                    cancellationTokenSource.Cancel();
                    return;
                }
            }
        });

        return cancellationTokenSource.Token;
    }
}
