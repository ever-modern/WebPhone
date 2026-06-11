using Microsoft.Extensions.Logging;
using WebPhone.Domain;
using WebPhone.Services.Channels;

namespace WebPhone.Services.Background;

public record ConnectionEstablishedArgs(string UserId, string RequestId);

public sealed class IncomingConnectionsHandler(
    IMessagesChannel messagesChannel,
    PeerConnector peerConnector,
    ILogger<IncomingConnectionsHandler> logger
) : IAsyncDisposable
{
    CancellationTokenSource? _cts;
    Task? _readerTask;

    public void Start()
    {
        if (_cts is not null)
            return;

        _cts = new CancellationTokenSource();
        _readerTask = ReadAsync(_cts.Token);
    }

    async Task ReadAsync(CancellationToken ct)
    {
        using var reader = messagesChannel.Subscribe(m => m.Type is MessageType.ConnectionAttempt);

        await foreach (var message in reader.ReadAllAsync(ct))
        {
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
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_readerTask is not null)
            await _readerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _cts?.Dispose();
    }
}
