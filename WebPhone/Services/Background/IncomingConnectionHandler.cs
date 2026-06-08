using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using Microsoft.Extensions.Logging;
using WebPhone.Contract;
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

    readonly EventSource<ConnectionEstablishedArgs> _connectionEstablished = new();
    public INotifier<ConnectionEstablishedArgs> ConnectionEstablished => _connectionEstablished;

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
            var payload = message.SpecifyPayload<WebRtcOffer>();
            if (payload is not null)
                _ = peerConnector.HandleIncomingConnectionRequestAsync(
                    message.SenderClientId,
                    payload.Payload,
                    ct
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
