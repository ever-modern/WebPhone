using WebPhone.Registration;

namespace WebPhone.Services;

public sealed class IncomingConnectionsHandler(
    IMessagesChannel messagesChannel,
    RtcConnector rtcConnector,
    ILogger<IncomingConnectionsHandler> logger) : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _readerTask;

    public event Action<string, string, IRtcConnection>? ConnectionEstablished;

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
            if (call is null) continue;

            try
            {
                var connection = await rtcConnector.AcceptConnectionAsync(
                    call.SenderClientId, call.Payload.ConnectionId, call.Payload.Offer, ct);
                ConnectionEstablished?.Invoke(call.SenderClientId, call.Payload.FromName, connection);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to accept connection from {Peer}", call.SenderClientId);
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
