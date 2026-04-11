using System.Collections.Concurrent;
using System.Text.Json;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;

namespace WebPhone.Services;

public sealed class WebRtcConnectionCoordinator : IAsyncDisposable
{
    private readonly WebRtcConnector webRtcConnector;
    private readonly IMessagesChannel messagesChannel;
    private readonly BackendClient backendClient;
    private readonly IProfile profile;
    private readonly ILogger<WebRtcConnectionCoordinator> logger;
    private readonly ConcurrentDictionary<string, PeerSession> _sessions = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly EventSource _stateChanged = new();
    private readonly Task _signalingReadTask;

    public INotifier StateChanged => _stateChanged;

    public string DisplayName => profile.User.Name;

    public WebRtcConnectionCoordinator(
        WebRtcConnector webRtcConnector,
        IMessagesChannel messagesChannel,
        BackendClient backendClient,
        IProfile profile,
        ILogger<WebRtcConnectionCoordinator> logger
    )
    {
        this.webRtcConnector = webRtcConnector;
        this.messagesChannel = messagesChannel;
        this.backendClient = backendClient;
        this.profile = profile;
        this.logger = logger;
        _signalingReadTask = Task.Run(() => ReadSignalingAsync(_cts.Token));
    }

    public async Task ConnectToUserAsync(string userId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(userId, out var existing) && existing.State is RtcConnectionState.Connected or RtcConnectionState.Connecting)
            return;

        var connectionId = Guid.NewGuid().ToString("N");
        var agent = await webRtcConnector.InitiateConnectionAsync(async offer =>
        {
            await messagesChannel.Writer.WriteAsync(
                new OutgoingMessage<ConnectionRequestPayload>(
                    MessageType.ConnectionAttempt,
                    new(connectionId, offer),
                    userId
                ),
                cancellationToken
            );

            using var channelReader = messagesChannel.Subscribe(m =>
            {
                if (m.Type is MessageType.ConnectionRejected && m.SenderClientId == userId)
                    return true;
                if (m.Type != MessageType.ConnectionAccepted || m.SenderClientId != userId)
                    return false;
                var specific = m.SpecifyPayload<AnswerPayload>();
                return specific?.Payload.RequestId == connectionId;
            });

            var response = await channelReader.ReadAsync(cancellationToken);
            if (response.Type is MessageType.ConnectionRejected)
                throw new InvalidOperationException($"Connection request to {userId} has been rejected.");

            var payload = response.SpecifyPayload<AnswerPayload>()?.Payload;
            if (payload?.Answer is null || string.IsNullOrWhiteSpace(payload.Answer.Type) || string.IsNullOrWhiteSpace(payload.Answer.Sdp))
                throw new InvalidOperationException($"Connection response from {userId} does not contain a valid answer.");

            return payload.Answer;
        });

        await RegisterSessionAsync(userId, connectionId, agent);
    }

    public async Task DisconnectAsync(string userId)
    {
        try
        {
            await messagesChannel.Writer.WriteAsync(
                new OutgoingMessage(MessageType.ConnectionClosed, JsonSerializer.SerializeToElement(new { }), userId)
            );
        }
        catch
        {
        }

        await RemoveSessionAsync(userId);
    }

    public RtcConnectionState GetConnectionState(string userId)
    {
        if (_sessions.TryGetValue(userId, out var session))
            return session.State;
        return RtcConnectionState.Closed;
    }

    public async Task SendBytesAsync(string userId, byte[] bytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        await ConnectToUserAsync(userId, cancellationToken);

        if (_sessions.TryGetValue(userId, out var session))
            await session.Agent.WriteBytesAsync(bytes);
    }

    public async Task<Subscription> SubscribeBytesAsync(string userId, Action<byte[]> onBytesReceived, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onBytesReceived);
        await ConnectToUserAsync(userId, cancellationToken);

        if (!_sessions.TryGetValue(userId, out var session))
            throw new InvalidOperationException($"No connection session found for user {userId}.");

        return await session.Agent.SubscribeBytesAsync(onBytesReceived);
    }

    public async Task NotifyClientAsync(string targetClientId, string? message = null, CancellationToken cancellationToken = default)
        => await backendClient.NotifyAsync(targetClientId, message, cancellationToken);

    public Task NotifySelfAsync(string? message = null, CancellationToken cancellationToken = default)
        => backendClient.NotifyAsync(null, message, cancellationToken);

    private async Task RegisterSessionAsync(string userId, string connectionId, RtcConnectionAgent agent)
    {
        await RemoveSessionAsync(userId);

        var session = new PeerSession(userId, connectionId, agent)
        {
            State = RtcConnectionState.Connecting,
        };

        session.StateSubscription = agent.StateChanged.Subscribe(state =>
        {
            session.State = MapState(state);
            _stateChanged.Invoke();
        });

        _sessions[userId] = session;
        _stateChanged.Invoke();
    }

    private async Task RemoveSessionAsync(string userId)
    {
        if (!_sessions.TryRemove(userId, out var session))
            return;

        session.StateSubscription?.Dispose();
        await session.Agent.DisposeAsync();
        _stateChanged.Invoke();
    }

    private async Task ReadSignalingAsync(CancellationToken cancellationToken)
    {
        using var reader = messagesChannel.Subscribe(m =>
            m.Type is MessageType.ConnectionAttempt or MessageType.ConnectionClosed
        );

        await foreach (var message in reader.ReadAllAsync(cancellationToken))
        {
            if (message.Type is MessageType.ConnectionClosed)
            {
                await RemoveSessionAsync(message.SenderClientId);
                continue;
            }

            var connectionRequest = message.SpecifyPayload<ConnectionRequestPayload>();
            if (connectionRequest?.Payload is null)
                continue;

            try
            {
                var accepted = await webRtcConnector.AcceptConnectionAsync(connectionRequest.Payload.Offer);

                await messagesChannel.Writer.WriteAsync(
                    new OutgoingMessage<AnswerPayload>(
                        MessageType.ConnectionAccepted,
                        new(connectionRequest.Payload.RequestId, accepted.Answer),
                        message.SenderClientId
                    ),
                    cancellationToken
                );

                await RegisterSessionAsync(message.SenderClientId, connectionRequest.Payload.RequestId, accepted.Connection);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to accept incoming WebRTC connection from {Peer}", message.SenderClientId);
            }
        }
    }

    private static RtcConnectionState MapState(string state) => state.ToLowerInvariant() switch
    {
        "new" => RtcConnectionState.New,
        "connecting" => RtcConnectionState.Connecting,
        "connected" => RtcConnectionState.Connected,
        "disconnected" => RtcConnectionState.Disconnected,
        "failed" => RtcConnectionState.Failed,
        "closed" => RtcConnectionState.Closed,
        _ => RtcConnectionState.Connecting
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        try
        {
            await _signalingReadTask;
        }
        catch
        {
        }

        foreach (var userId in _sessions.Keys.ToArray())
            await RemoveSessionAsync(userId);

        _cts.Dispose();
    }

    private sealed record PeerSession(string UserId, string ConnectionId, RtcConnectionAgent Agent)
    {
        public RtcConnectionState State { get; set; }

        public IDisposable? StateSubscription { get; set; }
    }
}
