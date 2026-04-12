using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using WebPhone.Services.Channels;

namespace WebPhone.Services.Background;

class CallMaintainer(RtcConnection rtcConnectionAgent)
{
    public Subscription SubscribeForCallMaintenance(
        Func<RtcMessageType> getMessageType,
        CancellationToken cancellationToken
    )
    {
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var connectionAgent = rtcConnectionAgent;
        var incomingChannel = new RtcConnectionMessageChannel(connectionAgent);
        var receivingCts = new CancellationTokenSource();
        var incomingMaintenance = incomingChannel.WhileReceiving(
            m => m.Type == RtcMessageType.MaintainingCall,
            TimeSpan.FromMilliseconds(500),
            receivingCts.Token
        );

        var callCts = CancellationTokenSource.CreateLinkedTokenSource(incomingMaintenance);
        var messageType = getMessageType();

        _ = Task.Run(
            async () =>
            {
                await using var channel = new RtcConnectionMessageChannel(connectionAgent);
                while (await timer.WaitForNextTickAsync(callCts.Token))
                {
                    await channel.Writer.WriteAsync(new(messageType, null));
                }
            },
            cancellationToken
        );
        var sub = new Subscription(() =>
        {
            receivingCts.Cancel();
            incomingChannel.Dispose();
            connectionAgent.DisableAudioInputAsync().ConfigureAwait(false);
            connectionAgent.DisableAudioOutputAsync().ConfigureAwait(false);
            connectionAgent.DisableVideoInputAsync().ConfigureAwait(false);
            connectionAgent.DisableVideoOutputAsync().ConfigureAwait(false);
        });
        return sub;
    }
}
