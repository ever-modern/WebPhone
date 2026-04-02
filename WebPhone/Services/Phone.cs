using EverModern.Blazor.DirectCommunication;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using WebPhone.Registration;

namespace WebPhone.Services;


public sealed class Phone(
    WebRtcInterop webRtc,
    IJSRuntime jsRuntime,
    ILoggerFactory loggerFactory,
    PhoneOptions options,
    IMessagesChannel externalChannel,
    RtcConnector rtcConnector,
    BackendClient backendClient,
    IProfile profile) : IAsyncDisposable
{
    private readonly Stopwatch stepTimer = Stopwatch.StartNew();
    private readonly int pollIntervalMs = Math.Max(options.PollIntervalMs, 250);
    private DateTimeOffset lastOutgoingTimestamp = DateTimeOffset.UtcNow;
    private long lastStepTimestamp;
    private PeriodicTimer? presenceTimer;
    private CancellationTokenSource? presenceCts;
    private Task? messageReaderTask;
    private CancellationTokenSource? messageReaderCts;
    private bool dataMessageSubscribed;

    public event Action? StateChanged;

    readonly ConcurrentDictionary<string, IRtcConnection> _userConnections = [];
    readonly ConcurrentDictionary<string, OuterUser> _presences = [];
    readonly ConcurrentDictionary<string, RtcMessageChannel> _textChannels = [];
    readonly  ILogger<Phone> _logger = loggerFactory.CreateLogger<Phone>();

    public string DisplayName => profile.User.Name;

    public bool HasStoredProfileName { get; private set; }

    public string? SignalingStatus { get; private set; }

    public ElementReference RemoteAudio { get; set; }

    public string? AudioStatusMessage { get; private set; }

    public IncomingMessage<ConnectionRequestPayload>? CurrentCall { get; private set; }

    public IReadOnlyList<CallInfo> IncomingCalls { get; private set; } = [];

    public IReadOnlyList<OuterUser> Users => [.. _presences.Values];

    public async Task InitializeAsync()
    {
        _ = SubscribeForPushAsync(default).ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogWarning(t.Exception, "Push subscription failed");
            else
                _logger.LogInformation("Push subscription completed");

            return 0;
        });

        StartMessageReader();
        StartPresenceLoop();

        if (!dataMessageSubscribed)
        {
            webRtc.DataMessageReceived += HandleDataMessageReceived;
            webRtc.DataBytesMessageReceived += HandleDataBytesMessageReceived;
            dataMessageSubscribed = true;
        }

        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            await SendPresenceAsync();
        }
    }

    public async Task<IRtcConnection> ConnectToUserAsync(string userId, CancellationToken cancellationToken)
    {
        var connection = await rtcConnector.InitiateConnectionAsync(userId, DisplayName, cancellationToken);
        TrackConnection(userId, connection);
        return connection;
    }

    public async Task CancelConnectionAsync(string userId)
    {
        await rtcConnector.CancelConnectionAsync(userId);

        if (_textChannels.TryRemove(userId, out var textChannel))
        {
            await textChannel.DisposeAsync();
        }

        if (_userConnections.TryRemove(userId, out var connection))
        {
            connection.Dispose();
        }

        if (_presences.TryGetValue(userId, out var existingPresence))
        {
            _presences[userId] = existingPresence with { ConnectionState = RtcConnectionState.Closed };
        }

        NotifyStateChanged();
    }

    public async Task<CallAgent> CreateCallAgentAsync(string targetClientId, CancellationToken cancellationToken = default)
    {
        var connection = await ConnectToUserAsync(targetClientId, cancellationToken);
        var textChannel = await GetTextChannelAsync(targetClientId, cancellationToken);
        var callAgent = new CallAgent(webRtc, externalChannel, textChannel, connection, loggerFactory.CreateLogger<CallAgent>());
        return callAgent;
    }

    public async Task SubscribeForPushAsync(CancellationToken cancellationToken)
    {
        var resultJson = await jsRuntime.InvokeAsync<string?>("registerPush", [Contract.VapidKeys.Public]);

        if (string.IsNullOrWhiteSpace(resultJson))
        {
            _logger.LogInformation("Push subscription skipped for this browser/session.");
            return;
        }

        await backendClient.RegisterPushSubscriptionAsync(resultJson, cancellationToken);
    }

    public async Task<RtcMessageChannel> GetTextChannelAsync(string userId, CancellationToken cancellationToken)
    {
        if (_textChannels.TryGetValue(userId, out var existingChannel))
        {
            return existingChannel;
        }

        var connection = await ConnectToUserAsync(userId, cancellationToken);
        while (true)
        {
            var channel = new RtcMessageChannel(connection, webRtc);
            if (_textChannels.TryAdd(userId, channel))
            {
                return channel;
            }

            await channel.DisposeAsync();

            if (_textChannels.TryGetValue(userId, out existingChannel))
            {
                return existingChannel;
            }
        }
    }

    private async Task SendPresenceAsync()
    {
        var payload = JsonSerializer.SerializeToElement(new PresencePayload(DisplayName));
        await externalChannel.Writer.WriteAsync(new WebPhone.Registration.OutgoingMessage(MessageType.Presence, payload, null));
        lastOutgoingTimestamp = DateTimeOffset.UtcNow;
    }

    void StartPresenceLoop()
    {
        presenceCts?.Cancel();
        presenceCts = new CancellationTokenSource();
        presenceTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        _ = RunPresenceLoopAsync(presenceCts.Token);
    }

    async Task RunPresenceLoopAsync(CancellationToken cancellationToken)
    {
        while (await presenceTimer.WaitForNextTickAsync(cancellationToken))
        {
            // Only send presence if no outgoing messages have been pushed within the configured poll interval
            var elapsed = DateTimeOffset.UtcNow - lastOutgoingTimestamp;
            if (elapsed >= TimeSpan.FromMilliseconds(pollIntervalMs))
            {
                await SendPresenceAsync();
            }
        }
    }

    private void StartMessageReader()
    {
        messageReaderCts?.Cancel();
        messageReaderCts = new CancellationTokenSource();
        messageReaderTask = ReadMessagesAsync(messageReaderCts.Token);
    }

    private async Task ReadMessagesAsync(CancellationToken cancellationToken)
    {
        using var reader = externalChannel.Subscribe();
        await foreach (var message in reader.ReadAllAsync(cancellationToken))
        {
            await HandleSignalingPayloadAsync(message);
        }
    }

    private async Task HandleSignalingPayloadAsync(IncomingMessage message)
    {
        switch (message.Type)
        {
            case MessageType.Presence:
                var presence = message.SpecifyPayload<PresencePayload>();
                if (presence is null)
                    break;
                var connection = _userConnections.GetValueOrDefault(presence.SenderClientId);
                _presences[presence.SenderClientId] = new OuterUser(presence.SenderClientId, presence.Payload.Name, presence.DateTime, connection?.State ?? RtcConnectionState.Closed);
                PrunePresence();
                LogStep($"Presence received from {presence.SenderClientId}");
                NotifyStateChanged();
                break;
            case MessageType.ConnectionAttempt:
                var call = message.SpecifyPayload<ConnectionRequestPayload>();
                if (call is null)
                    break;
                SignalingStatus = $"Incoming connection request from {call.Payload.FromName}...";
                var acceptedConnection = await rtcConnector.AcceptConnectionAsync(call.SenderClientId, call.Payload.ConnectionId, call.Payload.Offer);
                TrackConnection(call.SenderClientId, acceptedConnection);
                LogStep("Incoming call received");
                NotifyStateChanged();
                break;
            case MessageType.Call:
                var callRequest = message.SpecifyPayload<InitiateCallPayload>();
                if (callRequest is null)
                    break;
                break;
        }
    }

    private void PrunePresence()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-30);
        foreach (var (key, value) in _presences.Where(user => user.Value.LastSeen < cutoff).ToArray())
        {
            _presences.TryRemove(key, out var _);
        }
    }

    private void LogStep(string step)
    {
        var elapsed = stepTimer.ElapsedMilliseconds;
        var delta = elapsed - lastStepTimestamp;
        lastStepTimestamp = elapsed;

        if (delta > 1000)
        {
            _logger.LogWarning("WebRTC step '{Step}' after {Elapsed}ms (+{Delta}ms)", step, elapsed, delta);
        }
        else
        {
            _logger.LogInformation("WebRTC step '{Step}' after {Elapsed}ms (+{Delta}ms)", step, elapsed, delta);
        }
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

    private void HandleDataMessageReceived(object? sender, WebRtcDataMessageEventArgs e)
    {
        var textChannel = ResolveTextChannelByConnectionId(e.ConnectionId);
        textChannel?.OnRawMessageReceived(e.Message);
    }

    private void HandleDataBytesMessageReceived(object? sender, WebRtcDataBytesMessageEventArgs e)
    {
        var textChannel = ResolveTextChannelByConnectionId(e.ConnectionId);
        textChannel?.OnRawMessageReceived(e.Message);
    }

    private RtcMessageChannel? ResolveTextChannelByConnectionId(string connectionId)
    {
        var existing = _textChannels.FirstOrDefault(x => x.Value.ConnectionId == connectionId).Value;
        if (existing is not null)
        {
            return existing;
        }

        var userId = _userConnections.FirstOrDefault(x => x.Value.Id == connectionId).Key;
        if (string.IsNullOrWhiteSpace(userId) || !_userConnections.TryGetValue(userId, out var connection))
        {
            return null;
        }

        var created = new RtcMessageChannel(connection, webRtc);
        if (_textChannels.TryAdd(userId, created))
        {
            return created;
        }

        _ = created.DisposeAsync();
        return _textChannels[userId];
    }

    private void TrackConnection(string userId, IRtcConnection connection)
    {
        _userConnections[userId] = connection;
        connection.StateChanged += state =>
        {
            if (_presences.TryGetValue(userId, out var userPresence))
            {
                _presences[userId] = userPresence with { ConnectionState = state };
            }

            NotifyStateChanged();
        };

        if (_presences.TryGetValue(userId, out var existingPresence))
        {
            _presences[userId] = existingPresence with { ConnectionState = connection.State };
        }

        NotifyStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        presenceCts?.Cancel();
        presenceTimer?.Dispose();
        messageReaderCts?.Cancel();
        if (messageReaderTask is not null)
        {
            await messageReaderTask;
        }

        foreach (var (_, connection) in _userConnections)
            connection.Dispose();

        foreach (var (_, channel) in _textChannels)
        {
            await channel.DisposeAsync();
        }

        if (dataMessageSubscribed)
        {
            webRtc.DataMessageReceived -= HandleDataMessageReceived;
            webRtc.DataBytesMessageReceived -= HandleDataBytesMessageReceived;
            dataMessageSubscribed = false;
        }
    }
}


public record User(string Id, string Name);

public record UserConnection(User OtherUser, IRtcConnection RtcConnection);

public record OuterUser(string Id, string Name, DateTimeOffset LastSeen, RtcConnectionState ConnectionState) : User(Id, Name);

public record CallInfo(string ConnectionId, string RemotePeerId, string RemotePeerName);

public record RtcTextMessage(string Text, bool IsSystem);

public sealed record UserPresence(string UserId, string Name, DateTimeOffset LastSeen);

public sealed record PresencePayload(string Name);

public sealed record HungupPayload(string CallId);

public sealed record ConnectionRequestPayload(string ConnectionId, string FromName, WebRtcOffer Offer);

public sealed record AnswerPayload(string ConnectionId, WebRtcAnswer Answer);

public sealed record InitiateCallPayload(string ConnectionId);

public sealed record CallResponsePayload(string ConnectionId, bool Accepted);
     