using System.Diagnostics;
using EverModern.Blazor.DirectCommunication;
using WebPhone.Services.Channels;

namespace WebPhone.Services;

record struct CallOptions(bool IsVideoCall);

class CallMaintainer(IRtcConnection connection, TimeSpan criticalTime)
{
    // Returns true when the received ping is a video-call ping.
    public Task<CallOptions> WhenReceivedCallPingAsync(
        CancellationToken cancellationToken = default
    )
    {
        var tcs = new TaskCompletionSource<CallOptions>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        _ = Task.Run(
            async () =>
            {
                using var channel = new RtcConnectionMessageChannel(connection);
                using var callDesireReader = channel.Subscribe(message =>
                    message.Type is RtcMessageType.WantCall or RtcMessageType.WantVideoCall
                );
                var msg = await callDesireReader
                    .ReadAllAsync(cancellationToken)
                    .FirstOrDefaultAsync(cancellationToken);
                tcs.TrySetResult(new(msg.Type is RtcMessageType.WantVideoCall));
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
                    message.Type
                        is RtcMessageType.WantCall
                            or RtcMessageType.WantVideoCall
                            or RtcMessageType.RejectCall
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

    public Task MaintainCallAsync(
        bool isVideo = false,
        CancellationToken cancellationToken = default
    )
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pingType = isVideo ? RtcMessageType.WantVideoCall : RtcMessageType.WantCall;

        var __ = WhenCallStoppedAsync(cts.Token)
            .ContinueWith(_ =>
            {
                cts.Cancel();
                tcs.TrySetResult();
            });

        if (cts.Token.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        _ = Task.Run(
            async () =>
            {
                using var channel = new RtcConnectionMessageChannel(connection);

                var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

                while (await timer.WaitForNextTickAsync(cts.Token))
                {
                    await channel.Writer.WriteAsync(new RtcMessage(pingType, null), cts.Token);
                }

                tcs.SetResult();
            },
            cancellationToken
        );

        return tcs.Task;
    }
}
