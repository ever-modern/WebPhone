using EverModern.Events;
using WebPhone.Messages;
using WebPhone.Services.Channels;
using WebPhone.Services.Connectivity;

namespace WebPhone.Services.Background;

public record ConnectionEstablishedArgs(string UserId, string RequestId);

public sealed class IncomingConnectionsHandler(
    IMessagesChannel messagesChannel,
    PeerConnector peerConnector,
    ILogger<IncomingConnectionsHandler> logger
) : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _readerTask;

    readonly EventSource<ConnectionEstablishedArgs> _connectionEstablished = new();
    public INotifier<ConnectionEstablishedArgs> ConnectionEstablished => _connectionEstablished;

    public void Start()
    {
        if (_cts is not null)
            return;

        _cts = new CancellationTokenSource();
        _readerTask = ReadAsync(_cts.Token);
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        using var reader = messagesChannel.Subscribe(m =>
            m.Type is MessageType.ConnectionAttempt or MessageType.ConnectionClosed
        );

        await foreach (var message in reader.ReadAllAsync(ct))
        {
            if (message.Type is MessageType.ConnectionClosed)
            {
                await peerConnector.HandlePeerConnectionClosedAsync(message.SenderClientId);
                continue;
            }

            var request = message.SpecifyPayload<ConnectionRequestPayload>();
            if (request?.Payload is null)
            {
                logger.LogWarning("[INCOMING] ConnectionAttempt has null payload from {Peer}", message.SenderClientId);
                continue;
            }

            try
            {
                await peerConnector.HandleIncomingConnectionRequestAsync(
                    message.SenderClientId,
                    request.Payload,
                    ct
                );

                _connectionEstablished.Invoke(
                    new ConnectionEstablishedArgs(message.SenderClientId, request.Payload.RequestId)
                );
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[INCOMING] Failed handling connection attempt from {Peer}", message.SenderClientId);
            }
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
