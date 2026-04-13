using System.Diagnostics;
using EverModern.Blazor.DirectCommunication;
using WebPhone.Services.Channels;

namespace WebPhone.Services;

class CallMaintainer(RtcConnection connection, TimeSpan criticalTime)
{
    public Task WhenReceivedCallPingAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(
            async () =>
            {
                using var channel = new RtcConnectionMessageChannel(connection);
                using var callDesireReader = channel.Subscribe(message =>
                    message.Type is RtcMessageType.WantCall
                );
                var _ = await callDesireReader.ReadAsync(cancellationToken);
                tcs.TrySetResult();
            },
            cancellationToken
        );

        return tcs.Task;
    }

    public Task WhenCallStoppedAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(
            async () =>
            {
                using var channel = new RtcConnectionMessageChannel(connection);
                using var callDesireReader = channel.Subscribe(message =>
                    message.Type is RtcMessageType.WantCall or RtcMessageType.RejectCall
                );

                var lastRequest = Stopwatch.GetTimestamp();

                var timer = new PeriodicTimer(TimeSpan.FromTicks(criticalTime.Ticks / 2));
                var __ = Task.Run(async () =>
                {
                    while (await timer.WaitForNextTickAsync(cts.Token))
                    {
                        var current = Stopwatch.GetElapsedTime(lastRequest);
                        if (current > criticalTime)
                        {
                            cts.Cancel();
                            tcs.TrySetResult();
                            break;
                        }
                    }
                });

                await foreach (var call in callDesireReader.ReadAllAsync(cts.Token))
                {
                    if (call.Type is RtcMessageType.RejectCall)
                    {
                        cts.Cancel();
                        tcs.SetResult();
                        return;
                    }
                    lastRequest = Stopwatch.GetTimestamp();
                }
            },
            cancellationToken
        );

        return tcs.Task;
    }

    public Task MaintainCallAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(
            async () =>
            {
                using var channel = new RtcConnectionMessageChannel(connection);
                using var callDesireReader = channel.Subscribe(message =>
                    message.Type is RtcMessageType.WantCall
                );

                var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

                while (await timer.WaitForNextTickAsync(cts.Token))
                {
                    await channel.Writer.WriteAsync(
                        new RtcMessage(RtcMessageType.WantCall, null),
                        cts.Token
                    );
                }

                tcs.SetResult();
            },
            cancellationToken
        );

        return tcs.Task;
    }
}
