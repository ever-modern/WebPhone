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

    event Action<RtcConnectionState> StateChanged;
}

public sealed class RtcConnector(WebRtcInterop webRtc, IMessagesChannel messagesChannel)
{
    readonly ConcurrentDictionary<string, RtcConnection> _connections = [];

    public async Task<IRtcConnection> InitiateConnectionAsync(
        string targetPeerId,
        string ownName,
        CancellationToken cancellationToken = default)
    {
        var value = await TryFindExistingConnectionAsync(targetPeerId);
        if (value is not null)
        {
            return value;
        }

        var connectionId = Guid.NewGuid().ToString("N");
        var connection = new RtcConnection(targetPeerId, connectionId, () => { _ = webRtc.CloseAsync(connectionId); });

        _connections[targetPeerId] = connection;

        var offer = await webRtc.CreateOfferAsync(connectionId);

        await messagesChannel.Writer.WriteAsync(
            new OutgoingMessage(
                Type: MessageType.ConnectionAttempt,
                Payload: JsonSerializer.SerializeToElement(new ConnectionRequestPayload(connectionId, ownName, offer)),
                TargetClientId: targetPeerId),
            cancellationToken
        );

        using var channelReader = messagesChannel
            .Subscribe(m =>
            {
                if (m.Type is MessageType.ConnectionRejected && m.SenderClientId == targetPeerId)
                {
                    return true;
                }

                if (m.Type != MessageType.ConnectionAccepted || m.SenderClientId != targetPeerId)
                {
                    return false;
                }

                var specific = m.SpecifyPayload<AnswerPayload>();

                if (specific?.Payload.ConnectionId != connectionId)
                {
                    return false;
                }

                return true;
            });


        var connectionResponse = await channelReader.ReadAsync(cancellationToken);

        if (connectionResponse.Type is MessageType.ConnectionRejected)
        {
            InvalidOperationException rejectedException = new($"Connection request to {targetPeerId} has been rejected.");
            connection.Connected.TrySetException(rejectedException);
            throw rejectedException;
        }

        var payload = connectionResponse.SpecifyPayload<AnswerPayload>()!.Payload;

        await webRtc.SetRemoteDescriptionAsync(connectionId, payload.Answer);

        await webRtc.InitializeAsync(connectionId, null).AsTask();

        connection.Connected.TrySetResult();

        return connection;
    }

    public async Task<IRtcConnection> AcceptConnectionAsync(
        string targetUserId,
        string connectionId,
        WebRtcOffer offer,
        CancellationToken cancellationToken = default)
    {
        var value = await TryFindExistingConnectionAsync(targetUserId);
        if (value is not null)
        {
            return value;
        }

        var connection = new RtcConnection(targetUserId, connectionId, () => { _ = webRtc.CloseAsync(connectionId); });

        _connections[targetUserId] = connection;

        var answer = await webRtc.CreateAnswerAsync(connectionId);

        await messagesChannel.Writer.WriteAsync(
            new OutgoingMessage<AnswerPayload>(
                Type: MessageType.ConnectionAttempt,
                Payload: new(connectionId, answer),
                TargetClientId: targetUserId),
            cancellationToken
        );

        await webRtc.SetRemoteDescriptionAsync(connectionId, offer);

        await webRtc.InitializeAsync(connectionId, null).AsTask();

        connection.Connected.TrySetResult();

        return connection;
    }

    private async Task<IRtcConnection?> TryFindExistingConnectionAsync(string targetUserId)
    {
        if (_connections.TryGetValue(targetUserId, out var readyConnection))
        {
            if (readyConnection.State is RtcConnectionState.Connected)
            {
                return readyConnection;
            }

            if (readyConnection.State is RtcConnectionState.Connecting or RtcConnectionState.New)
            {
                await readyConnection.Connected.Task;
                return readyConnection;
            }

            if (readyConnection.State is RtcConnectionState.Disconnected or RtcConnectionState.Closed or RtcConnectionState.Failed)
            {
                _connections.TryRemove(targetUserId, out _);
            }
        }

        return null;
    }

    record RtcConnection(
        string RemotePeer,
        string Id,
        Action Dispose) : IRtcConnection
    {
        public RtcConnectionState State { get; set; } = RtcConnectionState.New;

        public TaskCompletionSource Connected { get; } = new();

        public event Action<RtcConnectionState> StateChanged = delegate { };

        void IDisposable.Dispose()
            => Dispose();
    }

}
