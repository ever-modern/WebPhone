using System.Collections.Concurrent;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using EverModern.Threading.Queues;

namespace WebPhone.Services;

public enum RtcConnectionStage
{
    Idle,
    Initiating,
    AwaitingAnswer,
    Accepting,
    Connected,
    Failed,
}


public class RtcConnectionProcess(
    INotifier<RtcConnectionStage> stageChangeNotifier,
    Func<RtcConnectionStage> getStage,
    Func<IRtcConnection> getResult
)
{
    Subscription? _sub;
    readonly Lock _lock = new();

    public IRtcConnection Result => getResult();
    public RtcConnectionStage Stage => getStage();

    public void Dispose()
    {
        lock (_lock)
        {
            _sub?.Dispose();
        }
    }

    public Task<IRtcConnection> WhenCompleted()
    {
        using var _ = new LockedScope();

        if (Stage is RtcConnectionStage.Connected)
            return Task.FromResult(Result);
        if (Stage is RtcConnectionStage.Failed)
            return Task.FromException<IRtcConnection>(
                new InvalidOperationException("RTC connection process failed.")
            );

        var tcs = new TaskCompletionSource<IRtcConnection>();
        _sub = stageChangeNotifier.Subscribe(_ =>
        {
            if (getStage() is RtcConnectionStage.Connected)
                tcs.TrySetResult(Result);
            else if (getStage() is RtcConnectionStage.Failed)
                tcs.TrySetException(
                    new InvalidOperationException("RTC connection process failed.")
                );
        });
        return tcs.Task;
    }
}

public enum RtcConnectionState
{
    New,
    Connecting,
    Connected,
    Disconnected,
    Recovering,
    Failed,
    Closed,
}

public record RtcSubscriptions(
    EventHandler<WebRtcConnectionStateChangedEventArgs>? ConnectionStateChanged = null,
    EventHandler<WebRtcDataChannelStateChangedEventArgs>? DataChannelStateChanged = null,
    EventHandler<WebRtcDataMessageEventArgs>? DataMessageReceived = null,
    EventHandler<WebRtcRemoteStreamEventArgs>? RemoteStreamAvailable = null
);

public interface IRtcConnection : IDisposable
{
    string Id { get; }

    string RemotePeer { get; }

    RtcConnectionState State { get; }

    void SetState(RtcConnectionState state);

    event Action<RtcConnectionState> StateChanged;
}

public sealed class RtcConnector
{
    readonly WebRtcInterop webRtc;
    readonly IMessagesChannel messagesChannel;
    readonly PhoneOptions _options;
    readonly ConcurrentDictionary<string, RtcConnection> _connections = [];
    readonly ILogger<RtcConnector> _logger;

    public RtcConnector(
        WebRtcInterop webRtc,
        IMessagesChannel messagesChannel,
        PhoneOptions options,
        ILogger<RtcConnector> logger
    )
    {
        this.webRtc = webRtc;
        this.messagesChannel = messagesChannel;
        _options = options;
        _logger = logger;
        webRtc.ConnectionStateChanged += HandleConnectionStateChanged;
    }

    public async Task<IRtcConnection> InitiateConnectionAsync(
        string targetPeerId,
        string ownName,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "[INITIATOR] InitiateConnectionAsync called for peer: {TargetPeerId}",
            targetPeerId
        );

        var existing = await TryFindExistingConnectionAsync(targetPeerId, cancellationToken);
        if (existing is not null)
        {
            _logger.LogDebug(
                "[INITIATOR] Found existing connection for peer: {TargetPeerId}, state: {State}",
                targetPeerId,
                existing.State
            );
            return existing;
        }

        var connection = await CreateRawInitiatedConnectionAsync(
            targetPeerId,
            ownName,
            cancellationToken
        );
        _connections[targetPeerId] = connection;
        _logger.LogDebug(
            "[INITIATOR] Connection created and tracked for peer: {TargetPeerId}",
            targetPeerId
        );
        return connection;
    }

    public Task CancelConnectionAsync(string targetUserId)
    {
        if (_connections.TryRemove(targetUserId, out var retained))
            retained.Dispose();
        return Task.CompletedTask;
    }

    public async Task<IRtcConnection> AcceptConnectionAsync(
        string targetUserId,
        string connectionId,
        WebRtcOffer offer,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "[ACCEPTOR] AcceptConnectionAsync called for peer: {TargetUserId}, connectionId: {ConnectionId}",
            targetUserId,
            connectionId
        );

        if (
            _connections.TryGetValue(targetUserId, out var existingConnection)
            && existingConnection.Id != connectionId
        )
        {
            _logger.LogDebug(
                "[ACCEPTOR] Removing stale connection for peer: {TargetUserId}, old connectionId: {OldConnectionId}",
                targetUserId,
                existingConnection.Id
            );
            _connections.TryRemove(targetUserId, out _);
            existingConnection.Connected.TrySetCanceled();
            existingConnection.Dispose();
        }

        var existing = await TryFindExistingConnectionAsync(targetUserId, cancellationToken);
        if (existing is not null)
        {
            _logger.LogDebug(
                "[ACCEPTOR] Found existing connection for peer: {TargetUserId}, state: {State}",
                targetUserId,
                existing.State
            );
            return existing;
        }

        var connection = await CreateRawAcceptedConnectionAsync(
            targetUserId,
            connectionId,
            offer,
            cancellationToken
        );
        _connections[targetUserId] = connection;
        _logger.LogDebug(
            "[ACCEPTOR] Connection created and tracked for peer: {TargetUserId}",
            targetUserId
        );
        return connection;
    }

    private async Task<RtcConnection> CreateRawInitiatedConnectionAsync(
        string targetPeerId,
        string ownName,
        CancellationToken cancellationToken
    )
    {
        var connectionId = Guid.NewGuid().ToString("N");
        _logger.LogDebug(
            "[INITIATOR] CreateRawInitiatedConnectionAsync - Generated connectionId: {ConnectionId} for peer: {TargetPeerId}",
            connectionId,
            targetPeerId
        );

        var connection = new RtcConnection(
            targetPeerId,
            connectionId,
            () =>
            {
                _ = webRtc.CloseAsync(connectionId);
            }
        );
        connection.SetState(RtcConnectionState.Connecting);
        _logger.LogDebug(
            "[INITIATOR] State set to Connecting for connectionId: {ConnectionId}",
            connectionId
        );

        _logger.LogDebug(
            "[INITIATOR] Initializing WebRTC for connectionId: {ConnectionId}",
            connectionId
        );
        await webRtc.InitializeAsync(connectionId, _options.WebRtcIceServers).AsTask();

        _logger.LogDebug(
            "[INITIATOR] Creating data channel for connectionId: {ConnectionId}",
            connectionId
        );
        await webRtc.CreateDataChannelAsync(connectionId, "chat");

        _logger.LogDebug(
            "[INITIATOR] Creating offer for connectionId: {ConnectionId}",
            connectionId
        );
        var offer = await webRtc.CreateOfferAsync(connectionId);

        _logger.LogDebug(
            "[INITIATOR] Sending ConnectionAttempt message to peer: {TargetPeerId}",
            targetPeerId
        );
        await messagesChannel.Writer.WriteAsync(
            new OutgoingMessage<ConnectionRequestPayload>(
                Type: MessageType.ConnectionAttempt,
                Payload: new(connectionId, ownName, offer),
                TargetClientId: targetPeerId
            ),
            cancellationToken
        );

        using var channelReader = messagesChannel.Subscribe(m =>
        {
            if (m.Type is MessageType.ConnectionRejected && m.SenderClientId == targetPeerId)
                return true;
            if (m.Type != MessageType.ConnectionAccepted || m.SenderClientId != targetPeerId)
                return false;
            var specific = m.SpecifyPayload<AnswerPayload>();
            return specific?.Payload.ConnectionId == connectionId;
        });

        _logger.LogDebug("[INITIATOR] Waiting for answer from peer: {TargetPeerId}", targetPeerId);
        var connectionResponse = await channelReader.ReadAsync(cancellationToken);

        if (connectionResponse.Type is MessageType.ConnectionRejected)
        {
            _logger.LogWarning(
                "[INITIATOR] Connection rejected by peer: {TargetPeerId}",
                targetPeerId
            );
            throw new InvalidOperationException(
                $"Connection request to {targetPeerId} has been rejected."
            );
        }

        _logger.LogDebug("[INITIATOR] Received answer from peer: {TargetPeerId}", targetPeerId);
        var payload = connectionResponse.SpecifyPayload<AnswerPayload>()?.Payload;
        if (
            payload is null
            || payload.Answer is null
            || string.IsNullOrWhiteSpace(payload.Answer.Type)
            || string.IsNullOrWhiteSpace(payload.Answer.Sdp)
        )
        {
            _logger.LogError(
                "[INITIATOR] Invalid answer received from peer: {TargetPeerId}",
                targetPeerId
            );
            throw new InvalidOperationException(
                $"Connection response from {targetPeerId} does not contain a valid answer."
            );
        }

        _logger.LogDebug(
            "[INITIATOR] Setting remote description for connectionId: {ConnectionId}",
            connectionId
        );
        await webRtc.SetRemoteDescriptionAsync(connectionId, payload.Answer);

        _logger.LogDebug(
            "[INITIATOR] Remote description set. WebRTC negotiation complete for connectionId: {ConnectionId}. Waiting for actual connection...",
            connectionId
        );
        // DO NOT set state to Connected here - let HandleConnectionStateChanged do it when WebRTC actually connects
        // connection.SetState(RtcConnectionState.Connected);
        // connection.Connected.TrySetResult();
        return connection;
    }

    private async Task<RtcConnection> CreateRawAcceptedConnectionAsync(
        string targetUserId,
        string connectionId,
        WebRtcOffer offer,
        CancellationToken cancellationToken
    )
    {
        _logger.LogDebug(
            "[ACCEPTOR] CreateRawAcceptedConnectionAsync - connectionId: {ConnectionId} for peer: {TargetUserId}",
            connectionId,
            targetUserId
        );

        var connection = new RtcConnection(
            targetUserId,
            connectionId,
            () =>
            {
                _ = webRtc.CloseAsync(connectionId);
            }
        );
        connection.SetState(RtcConnectionState.Connecting);
        _logger.LogDebug(
            "[ACCEPTOR] State set to Connecting for connectionId: {ConnectionId}",
            connectionId
        );

        _logger.LogDebug(
            "[ACCEPTOR] Initializing WebRTC for connectionId: {ConnectionId}",
            connectionId
        );
        await webRtc.InitializeAsync(connectionId, _options.WebRtcIceServers).AsTask();

        _logger.LogDebug(
            "[ACCEPTOR] Setting remote description (offer) for connectionId: {ConnectionId}",
            connectionId
        );
        await webRtc.SetRemoteDescriptionAsync(connectionId, offer);

        _logger.LogDebug(
            "[ACCEPTOR] Creating answer for connectionId: {ConnectionId}",
            connectionId
        );
        var answer = await webRtc.CreateAnswerAsync(connectionId);

        _logger.LogDebug("[ACCEPTOR] Sending answer to peer: {TargetUserId}", targetUserId);
        await messagesChannel.Writer.WriteAsync(
            new OutgoingMessage<AnswerPayload>(
                Type: MessageType.ConnectionAccepted,
                Payload: new(connectionId, answer),
                TargetClientId: targetUserId
            ),
            cancellationToken
        );

        _logger.LogDebug(
            "[ACCEPTOR] Answer sent. WebRTC negotiation complete for connectionId: {ConnectionId}. Staying in Connecting state until WebRTC connection establishes.",
            connectionId
        );
        // FIXED: Do NOT set state to Connected here - let HandleConnectionStateChanged do it when WebRTC actually connects
        // connection.SetState(RtcConnectionState.Connected);
        // connection.Connected.TrySetResult();
        return connection;
    }

    private void HandleConnectionStateChanged(
        object? sender,
        WebRtcConnectionStateChangedEventArgs e
    )
    {
        _logger.LogDebug(
            "[WebRTC EVENT] ConnectionStateChanged - connectionId: {ConnectionId}, new state: {State}",
            e.ConnectionId,
            e.State
        );

        var found = _connections.FirstOrDefault(p => p.Value.Id == e.ConnectionId);
        var key = found.Key;
        var connection = found.Value;
        if (connection is null)
        {
            _logger.LogWarning(
                "[WebRTC EVENT] Connection not found for connectionId: {ConnectionId}",
                e.ConnectionId
            );
            return;
        }

        var mapped = e.State.ToLowerInvariant() switch
        {
            "new" => RtcConnectionState.New,
            "connecting" => RtcConnectionState.Connecting,
            "connected" => RtcConnectionState.Connected,
            "disconnected" => RtcConnectionState.Disconnected,
            "failed" => RtcConnectionState.Failed,
            "closed" => RtcConnectionState.Closed,
            _ => connection.State,
        };

        _logger.LogDebug(
            "[WebRTC EVENT] Setting connection state from {OldState} to {NewState} for peer: {RemotePeer}, connectionId: {ConnectionId}",
            connection.State,
            mapped,
            connection.RemotePeer,
            e.ConnectionId
        );

        connection.SetState(mapped);
        if (mapped is RtcConnectionState.Connected)
        {
            _logger.LogInformation(
                "[WebRTC EVENT] Connection ESTABLISHED for peer: {RemotePeer}, connectionId: {ConnectionId}",
                connection.RemotePeer,
                e.ConnectionId
            );
            connection.Connected.TrySetResult();
        }
        else if (mapped is RtcConnectionState.Failed)
        {
            _logger.LogError(
                "[WebRTC EVENT] Connection FAILED for peer: {RemotePeer}, connectionId: {ConnectionId}",
                connection.RemotePeer,
                e.ConnectionId
            );
            connection.Connected.TrySetException(
                new InvalidOperationException("WebRTC connection failed.")
            );
        }
        else if (mapped is RtcConnectionState.Closed)
        {
            _logger.LogWarning(
                "[WebRTC EVENT] Connection CLOSED for peer: {RemotePeer}, connectionId: {ConnectionId}",
                connection.RemotePeer,
                e.ConnectionId
            );
            connection.Connected.TrySetCanceled();
            if (!string.IsNullOrWhiteSpace(key))
                _connections.TryRemove(key, out _);
        }
    }

    private async Task<IRtcConnection?> TryFindExistingConnectionAsync(
        string targetUserId,
        CancellationToken cancellationToken = default
    )
    {
        if (!_connections.TryGetValue(targetUserId, out var connection))
            return null;

        if (connection.State is RtcConnectionState.Connected or RtcConnectionState.Disconnected)
            return connection;

        if (connection.State is RtcConnectionState.Connecting or RtcConnectionState.New)
        {
            await connection.Connected.Task.WaitAsync(cancellationToken);
            return connection;
        }

        if (connection.State is RtcConnectionState.Failed or RtcConnectionState.Closed)
            _connections.TryRemove(targetUserId, out _);

        return null;
    }

    sealed record RtcConnection(string RemotePeer, string Id, Action DisposeAction) : IRtcConnection
    {
        public RtcConnectionState State { get; private set; } = RtcConnectionState.New;

        public TaskCompletionSource Connected { get; } = new();

        public event Action<RtcConnectionState> StateChanged = delegate { };

        public void SetState(RtcConnectionState state)
        {
            if (State == state)
                return;
            State = state;
            StateChanged(state);
        }

        public void Dispose() => DisposeAction();
    }
}
