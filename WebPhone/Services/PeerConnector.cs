using System.Collections.Concurrent;
using System.Text.Json;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using WebPhone.Contract;
using WebPhone.Messages;
using WebPhone.Services.Channels;

namespace WebPhone.Services;

public sealed class PeerConnector : BackgroundProcessor
{
    private readonly RtcConnector _webRtcConnector;
    private readonly IMessagesChannel _messagesChannel;
    private readonly ILogger<PeerConnector> _logger;
    private readonly SemaphoreSlim _locker = new(1, 1);
    private readonly ConcurrentDictionary<string, AccountedConnection> _connections = new();
    private readonly EventSource _connectionEventSource = new();

    public INotifier StateChanged => _connectionEventSource;

    public IReadOnlyDictionary<string, RtcConnection> CurrentConnections =>
        _connections
            .Select(kvp =>
                (
                    kvp.Key,
                    kvp.Value.ConnectionTask.IsCompletedSuccessfully
                        ? kvp.Value.ConnectionTask.Result
                        : null!
                )
            )
            .Where(kvp => kvp.Item2 is not null)
            .ToDictionary(kvp => kvp.Item1, kvp => kvp.Item2!);

    public PeerConnector(
        RtcConnector webRtcConnector,
        IMessagesChannel messagesChannel,
        ILogger<PeerConnector> logger
    )
    {
        _webRtcConnector = webRtcConnector;
        _messagesChannel = messagesChannel;
        _logger = logger;
    }

    public async Task<RtcConnection> GetPeerConnectionAsync(
        string peerId,
        CancellationToken cancellationToken = default
    )
    {
        AccountedConnection connection;

        await _locker.WaitAsync(cancellationToken);
        try
        {
            if (_connections.TryGetValue(peerId, out connection!))
            {
                _logger.LogDebug(
                    "[PAIR] Returning existing connection slot for peer {PeerId}",
                    peerId
                );
            }
            else
            {
                var requestId = NewId();
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var task = CreateOutgoingConnectionAsync(peerId, requestId, linkedCts.Token);
                connection = new AccountedConnection(peerId, requestId, true, linkedCts, task);
                _connections[peerId] = connection;
                _connectionEventSource.Invoke();
                _logger.LogInformation(
                    "[PAIR] Created outgoing connection slot for peer {PeerId}, requestId {RequestId}",
                    peerId,
                    requestId
                );
            }
        }
        finally
        {
            _locker.Release();
        }

        try
        {
            return await connection.ConnectionTask.WaitAsync(cancellationToken);
        }
        catch
        {
            await RemoveConnectionIfMatchesAsync(peerId, connection.RequestId);
            throw;
        }
    }

    public async Task ClosePeerConnectionAsync(
        string peerId,
        CancellationToken cancellationToken = default
    )
    {
        await _messagesChannel.Writer.WriteAsync(
            new OutgoingMessage(MessageType.ConnectionClosed, new JsonElement(), peerId),
            cancellationToken
        );
        await RemoveConnectionAsync(peerId);
    }

    public async Task HandleIncomingConnectionRequestAsync(
        string peerId,
        ConnectionRequestPayload request,
        CancellationToken cancellationToken = default
    ) => await HandleIncomingAttemptAsync(peerId, request, cancellationToken);

    public async Task HandlePeerConnectionClosedAsync(string peerId) =>
        await RemoveConnectionAsync(peerId);

    private async Task<RtcConnection> CreateOutgoingConnectionAsync(
        string peerId,
        string requestId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var agent = await _webRtcConnector.InitiateConnectionAsync(async offer =>
            {
                await _messagesChannel.Writer.WriteAsync(
                    new OutgoingMessage<ConnectionRequestPayload>(
                        MessageType.ConnectionAttempt,
                        new(requestId, offer),
                        peerId
                    ),
                    cancellationToken
                );

                using var channelReader = _messagesChannel.Subscribe(m =>
                {
                    if (m.SenderClientId != peerId)
                        return false;

                    if (m.Type is MessageType.ConnectionAccepted)
                    {
                        var acceptedPayload = m.SpecifyPayload<AnswerPayload>();
                        return acceptedPayload?.Payload.RequestId == requestId;
                    }

                    if (m.Type is MessageType.ConnectionRejected)
                    {
                        var rejectedPayload = m.SpecifyPayload<ConnectionRejectedPayload>();
                        return rejectedPayload?.Payload.RequestId == requestId
                            || rejectedPayload is null;
                    }

                    return false;
                });

                var response = await channelReader.ReadAsync(cancellationToken);
                if (response.Type is MessageType.ConnectionRejected)
                    throw new InvalidOperationException(
                        $"Connection request to {peerId} has been rejected."
                    );

                var payload = response.SpecifyPayload<AnswerPayload>()?.Payload;
                if (
                    payload?.Answer is null
                    || string.IsNullOrWhiteSpace(payload.Answer.Type)
                    || string.IsNullOrWhiteSpace(payload.Answer.Sdp)
                )
                {
                    throw new InvalidOperationException(
                        $"Connection response from {peerId} does not contain a valid answer."
                    );
                }

                return payload.Answer;
            });

            _connectionEventSource.Invoke();

            return agent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Outgoing connection request {requestId} for peer {peerId} was superseded or canceled."
            );
        }
    }

    private async Task HandleIncomingAttemptAsync(
        string peerId,
        ConnectionRequestPayload incoming,
        CancellationToken cancellationToken
    )
    {
        AccountedConnection? connectionToDispose = null;
        bool shouldAcceptIncoming;

        await _locker.WaitAsync(cancellationToken);
        try
        {
            if (!_connections.TryGetValue(peerId, out var existing))
            {
                shouldAcceptIncoming = true;
            }
            else if (!existing.IsOutgoing)
            {
                // Already handling/holding an incoming-selected pairing for this peer.
                shouldAcceptIncoming = false;
            }
            else
            {
                var compare = string.CompareOrdinal(incoming.RequestId, existing.RequestId);
                shouldAcceptIncoming = compare > 0;

                if (shouldAcceptIncoming)
                {
                    existing.Cancellation.Cancel();
                    _connections.TryRemove(peerId, out _);
                    connectionToDispose = existing;
                    _logger.LogInformation(
                        "[PAIR] Collision for peer {PeerId}: incoming request {IncomingRequestId} won over local request {LocalRequestId}",
                        peerId,
                        incoming.RequestId,
                        existing.RequestId
                    );
                }
                else
                {
                    _logger.LogInformation(
                        "[PAIR] Collision for peer {PeerId}: local request {LocalRequestId} won over incoming request {IncomingRequestId}",
                        peerId,
                        existing.RequestId,
                        incoming.RequestId
                    );
                }
            }

            if (shouldAcceptIncoming)
            {
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var incomingTask = AcceptIncomingConnectionAsync(peerId, incoming, linkedCts.Token);
                _connections[peerId] = new AccountedConnection(
                    peerId,
                    incoming.RequestId,
                    false,
                    linkedCts,
                    incomingTask
                );
                _connectionEventSource.Invoke();
            }
        }
        finally
        {
            _locker.Release();
        }

        if (connectionToDispose is not null)
            await DisposeConnectionAgentIfReadyAsync(connectionToDispose);

        if (!shouldAcceptIncoming)
        {
            await _messagesChannel.Writer.WriteAsync(
                new OutgoingMessage<ConnectionRejectedPayload>(
                    MessageType.ConnectionRejected,
                    new(incoming.RequestId),
                    peerId
                ),
                cancellationToken
            );
        }
    }

    async Task<RtcConnection> AcceptIncomingConnectionAsync(
        string peerId,
        ConnectionRequestPayload incoming,
        CancellationToken cancellationToken
    )
    {
        var connection = await _webRtcConnector.AcceptConnectionAsync(
            incoming.Offer,
            async (answer) =>
                await _messagesChannel.Writer.WriteAsync(
                    new OutgoingMessage<AnswerPayload>(
                        MessageType.ConnectionAccepted,
                        new(incoming.RequestId, answer),
                        peerId
                    ),
                    cancellationToken
                )
        );

        _connectionEventSource.Invoke();
        return connection;
    }

    private async Task RemoveConnectionAsync(string peerId)
    {
        if (!_connections.TryRemove(peerId, out var existing))
            return;

        existing.Cancellation.Cancel();
        await DisposeConnectionAgentIfReadyAsync(existing);
        _connectionEventSource.Invoke();
    }

    private async Task RemoveConnectionIfMatchesAsync(string peerId, string requestId)
    {
        AccountedConnection? removed = null;

        await _locker.WaitAsync();
        try
        {
            if (
                _connections.TryGetValue(peerId, out var existing)
                && existing.RequestId == requestId
            )
            {
                _connections.TryRemove(peerId, out removed);
            }
        }
        finally
        {
            _locker.Release();
        }

        if (removed is null)
            return;

        removed.Cancellation.Cancel();
        await DisposeConnectionAgentIfReadyAsync(removed);
        _connectionEventSource.Invoke();
    }

    private static async Task DisposeConnectionAgentIfReadyAsync(AccountedConnection connection)
    {
        try
        {
            if (!connection.ConnectionTask.IsCompletedSuccessfully)
                return;

            await connection.ConnectionTask.Result.DisposeAsync();
        }
        catch { }
        finally
        {
            connection.Cancellation.Dispose();
        }
    }

    protected override void AfterDispose()
    {
        foreach (var (_, connection) in _connections)
        {
            connection.Cancellation.Cancel();
            _ = DisposeConnectionAgentIfReadyAsync(connection);
        }

        _connections.Clear();
    }

    private static string NewId() => CommonIdsGenerator.NewId().ToString();

    record AccountedConnection(
        string PeerId,
        string RequestId,
        bool IsOutgoing,
        CancellationTokenSource Cancellation,
        Task<RtcConnection> ConnectionTask
    );
}
