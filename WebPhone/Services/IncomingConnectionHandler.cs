using EverModern.Events;

namespace WebPhone.Services;

public record ConnectionEstablishedArgs(string UserId, string FromName, IRtcConnection Connection);

public sealed class IncomingConnectionsHandler(
    IMessagesChannel messagesChannel,
    RtcConnector rtcConnector,
    ILogger<IncomingConnectionsHandler> logger
) : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _readerTask;

    readonly EventSource<ConnectionEstablishedArgs> _connectionEstablished = new();
    public INotifier<ConnectionEstablishedArgs> ConnectionEstablished => _connectionEstablished;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _readerTask = ReadAsync(_cts.Token);
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        using var reader = messagesChannel.Subscribe(m => m.Type == MessageType.ConnectionAttempt);
        await foreach (var message in reader.ReadAllAsync(ct))
        {
            var call = message.SpecifyPayload<ConnectionRequestPayload>();
            if (call is null)
            {
                logger.LogWarning("[INCOMING] Received ConnectionAttempt with null payload");
                continue;
            }

            logger.LogInformation("[INCOMING] Received connection attempt from {Peer}, connectionId: {ConnectionId}", 
                call.SenderClientId, call.Payload.ConnectionId);

            try
            {
                var connection = await rtcConnector.AcceptConnectionAsync(
                    call.SenderClientId,
                    call.Payload.ConnectionId,
                    call.Payload.Offer,
                    ct
                );

                logger.LogInformation("[INCOMING] Connection accepted from {Peer}, connectionId: {ConnectionId}, state: {State}", 
                    call.SenderClientId, call.Payload.ConnectionId, connection.State);

                _connectionEstablished.Invoke(
                    new ConnectionEstablishedArgs(call.SenderClientId, call.Payload.FromName, connection)
                );
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[INCOMING] Failed to accept connection from {Peer}", call.SenderClientId);
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
