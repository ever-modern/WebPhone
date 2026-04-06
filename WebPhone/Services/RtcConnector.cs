using EverModern.Blazor.DirectCommunication;
using System.Collections.Concurrent;
using System.Text.Json;
using WebPhone.Registration;

namespace WebPhone.Services;

public enum RtcConnectionState
{
    New,
    Connecting,
    Connected,
    Disconnected,
    Recovering,
    Failed,
    Closed
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

    public RtcConnector(WebRtcInterop webRtc, IMessagesChannel messagesChannel, PhoneOptions options)
    {
        this.webRtc = webRtc;
        this.messagesChannel = messagesChannel;
        _options = options;
        webRtc.ConnectionStateChanged += HandleConnectionStateChanged;
    }

    public async Task<IRtcConnection> InitiateConnectionAsync(
        string targetPeerId,
        string ownName,
        CancellationToken cancellationToken = default)
    {
        var existing = await TryFindExistingConnectionAsync(targetPeerId, cancellationToken);
        if (existing is not null)
            return existing;

        var connection = await CreateRawInitiatedConnectionAsync(targetPeerId, ownName, cancellationToken);
        _connections[targetPeerId] = connection;
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
        CancellationToken cancellationToken = default)
    {
        if (_connections.TryGetValue(targetUserId, out var existingConnection) && existingConnection.Id != connectionId)
        {
            _connections.TryRemove(targetUserId, out _);
            existingConnection.Connected.TrySetCanceled();
            existingConnection.Dispose();
        }

        var existing = await TryFindExistingConnectionAsync(targetUserId, cancellationToken);
        if (existing is not null)
            return existing;

        var connection = await CreateRawAcceptedConnectionAsync(targetUserId, connectionId, offer, cancellationToken);
        _connections[targetUserId] = connection;
        return connection;
    }

    private async Task<RtcConnection> CreateRawInitiatedConnectionAsync(
        string targetPeerId,
        string ownName,
        CancellationToken cancellationToken)
    {
        var connectionId = Guid.NewGuid().ToString("N");
        var connection = new RtcConnection(targetPeerId, connectionId, () => { _ = webRtc.CloseAsync(connectionId); });
        connection.SetState(RtcConnectionState.Connecting);

        await webRtc.InitializeAsync(connectionId, _options.WebRtcIceServers).AsTask();
        await webRtc.CreateDataChannelAsync(connectionId, "chat");

        var offer = await webRtc.CreateOfferAsync(connectionId);

        await messagesChannel.Writer.WriteAsync(
            new OutgoingMessage(
                Type: MessageType.ConnectionAttempt,
                Payload: JsonSerializer.SerializeToElement(new ConnectionRequestPayload(connectionId, ownName, offer)),
                TargetClientId: targetPeerId),
            cancellationToken);

        using var channelReader = messagesChannel.Subscribe(m =>
        {
            if (m.Type is MessageType.ConnectionRejected && m.SenderClientId == targetPeerId)
                return true;
            if (m.Type != MessageType.ConnectionAccepted || m.SenderClientId != targetPeerId)
                return false;
            var specific = m.SpecifyPayload<AnswerPayload>();
            return specific?.Payload.ConnectionId == connectionId;
        });

        var connectionResponse = await channelReader.ReadAsync(cancellationToken);

        if (connectionResponse.Type is MessageType.ConnectionRejected)
            throw new InvalidOperationException($"Connection request to {targetPeerId} has been rejected.");

        var payload = connectionResponse.SpecifyPayload<AnswerPayload>()?.Payload;
        if (payload is null || payload.Answer is null
            || string.IsNullOrWhiteSpace(payload.Answer.Type)
            || string.IsNullOrWhiteSpace(payload.Answer.Sdp))
        {
            throw new InvalidOperationException($"Connection response from {targetPeerId} does not contain a valid answer.");
        }

        await webRtc.SetRemoteDescriptionAsync(connectionId, payload.Answer);

        connection.SetState(RtcConnectionState.Connected);
        connection.Connected.TrySetResult();
        return connection;
    }

    private async Task<RtcConnection> CreateRawAcceptedConnectionAsync(
        string targetUserId,
        string connectionId,
        WebRtcOffer offer,
        CancellationToken cancellationToken)
    {
        var connection = new RtcConnection(targetUserId, connectionId, () => { _ = webRtc.CloseAsync(connectionId); });
        connection.SetState(RtcConnectionState.Connecting);

        await webRtc.InitializeAsync(connectionId, _options.WebRtcIceServers).AsTask();
        await webRtc.SetRemoteDescriptionAsync(connectionId, offer);

        var answer = await webRtc.CreateAnswerAsync(connectionId);

        await messagesChannel.Writer.WriteAsync(
            new OutgoingMessage<AnswerPayload>(
                Type: MessageType.ConnectionAccepted,
                Payload: new(connectionId, answer),
                TargetClientId: targetUserId),
            cancellationToken);

        connection.SetState(RtcConnectionState.Connected);
        connection.Connected.TrySetResult();
        return connection;
    }

    private void HandleConnectionStateChanged(object? sender, WebRtcConnectionStateChangedEventArgs e)
    {
        var found = _connections.FirstOrDefault(p => p.Value.Id == e.ConnectionId);
        var key = found.Key;
        var connection = found.Value;
        if (connection is null)
            return;

        var mapped = e.State.ToLowerInvariant() switch
        {
            "new" => RtcConnectionState.New,
            "connecting" => RtcConnectionState.Connecting,
            "connected" => RtcConnectionState.Connected,
            "disconnected" => RtcConnectionState.Disconnected,
            "failed" => RtcConnectionState.Failed,
            "closed" => RtcConnectionState.Closed,
            _ => connection.State
        };

        connection.SetState(mapped);
        if (mapped is RtcConnectionState.Connected)
            connection.Connected.TrySetResult();
        else if (mapped is RtcConnectionState.Failed)
            connection.Connected.TrySetException(new InvalidOperationException("WebRTC connection failed."));
        else if (mapped is RtcConnectionState.Closed)
        {
            connection.Connected.TrySetCanceled();
            if (!string.IsNullOrWhiteSpace(key))
                _connections.TryRemove(key, out _);
        }
    }

    private async Task<IRtcConnection?> TryFindExistingConnectionAsync(
        string targetUserId,
        CancellationToken cancellationToken = default)
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

    sealed record RtcConnection(
        string RemotePeer,
        string Id,
        Action DisposeAction) : IRtcConnection
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
