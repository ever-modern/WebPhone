using Microsoft.Extensions.Logging;
using WebPhone.Domain;
using WebPhone.Services.Channels;

namespace WebPhone.Services.Background;

public sealed class IncomingConnectionsHandler(
    PeerConnector peerConnector,
    ILogger<IncomingConnectionsHandler> logger
) : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    readonly Lock _startedLock = new();

    public async Task<IncomingConnectionsHandler> StartReadingAsync(IMessagesChannel messagesChannel, CancellationToken ct = default)
    {
        if (_startedLock.TryEnter() is false)
            return this;

        ct.ThrowIfCancellationRequested();

        TaskCompletionSource started = new();

        _ = Task.Run(
            async () =>
            {
                using var reader = messagesChannel.Subscribe(m => m.Type is MessageType.ConnectionAttempt);

                await foreach (var message in reader.ReadAllAsync(_cts.Token).Prepend(null!))
                {
                    if (message is null)
                    {
                        started.TrySetResult();
                        continue;
                    }

                    var concreteMessage = message.SpecifyPayload<WebRtcOffer>();
                    if (concreteMessage is null)
                        continue;

                    logger.LogInformation(
                        "Received incoming connection attempt from {SenderClientId}. OfferType={OfferType}, HasSdp={HasSdp}",
                        message.SenderClientId,
                        concreteMessage.Payload.Type,
                        !string.IsNullOrWhiteSpace(concreteMessage.Payload.Sdp)
                    );

                    _ = peerConnector.ConnectToPeerAsync(
                        message.SenderClientId,
                        ct,
                        concreteMessage.Payload
                    );
                }
            },
            ct
        );

        await started.Task;

        return this;
    }

    public void Dispose()
    {
        if (_startedLock.TryEnter())
            return;

        _cts.Cancel();
        _cts.Dispose();
    }
}
