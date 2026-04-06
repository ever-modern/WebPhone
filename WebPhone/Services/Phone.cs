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
    IProfile profile,
    ContactsRepository contactsTracker,
    PresenceAnnouncer presenceAnnouncer,
    IncomingConnectionsHandler incomingConnectionHandler) : IAsyncDisposable
{
    private readonly Stopwatch stepTimer = Stopwatch.StartNew();
    private long lastStepTimestamp;
    private Task? messageReaderTask;
    private CancellationTokenSource? messageReaderCts;
    private bool dataMessageSubscribed;

    public event Action? StateChanged;

    readonly ConcurrentDictionary<string, IRtcConnection> _userConnections = [];
    readonly ConcurrentDictionary<string, RtcMessageChannel> _textChannels = [];
    readonly ILogger<Phone> _logger = loggerFactory.CreateLogger<Phone>();

    public string DisplayName => profile.User.Name;

    public bool HasStoredProfileName { get; private set; }

    public string? SignalingStatus { get; private set; }

    public ElementReference RemoteAudio { get; set; }

    public string? AudioStatusMessage { get; private set; }

    public IncomingMessage<ConnectionRequestPayload>? CurrentCall { get; private set; }

    public IReadOnlyList<CallInfo> IncomingCalls { get; private set; } = [];

    public IReadOnlyList<Contact> Users => contactsTracker.Contacts;

    public Task InitializeAsync()
    {
        contactsTracker.StateChanged += NotifyStateChanged;
        incomingConnectionHandler.ConnectionEstablished += OnConnectionEstablished;
        StartMessageReader();

        _ = SubscribeForPushAsync(default).ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogWarning(t.Exception, "Push subscription failed");
            else
                _logger.LogInformation("Push subscription completed");

            return 0;
        });

        if (!dataMessageSubscribed)
        {
            webRtc.DataMessageReceived += HandleDataMessageReceived;
            webRtc.DataBytesMessageReceived += HandleDataBytesMessageReceived;
            dataMessageSubscribed = true;
        }

        return Task.CompletedTask;
    }

    public async Task<IRtcConnection> ConnectToUserAsync(string userId, CancellationToken cancellationToken)
    {
        var connection = await rtcConnector.InitiateConnectionAsync(userId, DisplayName, cancellationToken);
        TrackConnection(userId, connection);
        return connection;
    }

    public async Task CancelConnectionAsync(string userId)
    {
        try
        {
            await externalChannel.Writer.WriteAsync(new OutgoingMessage(
                MessageType.ConnectionClosed,
                JsonSerializer.SerializeToElement(new { }),
                userId));
        }
        catch { }

        await CleanupPeerConnectionAsync(userId);
    }

    public async Task<CallAgent> CreateCallAgentAsync(string targetClientId, CancellationToken cancellationToken = default)
    {
        var connection = await ConnectToUserAsync(targetClientId, cancellationToken);
        var textChannel = await GetTextChannelAsync(targetClientId, cancellationToken);
        var callAgent = new CallAgent(webRtc, externalChannel, textChannel, connection, loggerFactory.CreateLogger<CallAgent>());
        return callAgent;
    }

    public async Task NotifyClientAsync(string targetClientId, string? message = null, CancellationToken cancellationToken = default)
    {
        await backendClient.NotifyAsync(targetClientId, message, cancellationToken);
    }

    public Task NotifySelfAsync(string? message = null, CancellationToken cancellationToken = default)
        => backendClient.NotifyAsync(null, message, cancellationToken);

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
            if (existingChannel.IsDisposed)
            {
                _textChannels.TryRemove(userId, out _);
            }
            else
            {
            return existingChannel;
            }
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

    private void StartMessageReader()
    {
        messageReaderCts?.Cancel();
        messageReaderCts = new CancellationTokenSource();
        messageReaderTask = ReadMessagesAsync(messageReaderCts.Token);
    }

    private async Task ReadMessagesAsync(CancellationToken cancellationToken)
    {
        using var reader = externalChannel.Subscribe(m => m.Type is MessageType.ConnectionClosed or MessageType.Call);
        await foreach (var message in reader.ReadAllAsync(cancellationToken))
        {
            await HandleSignalingPayloadAsync(message);
        }
    }

    private async Task HandleSignalingPayloadAsync(IncomingMessage message)
    {
        switch (message.Type)
        {
            case MessageType.ConnectionClosed:
                await CleanupPeerConnectionAsync(message.SenderClientId);
                break;
            case MessageType.Call:
                var callRequest = message.SpecifyPayload<InitiateCallPayload>();
                if (callRequest is null)
                    break;
                break;
        }
    }

    private void OnConnectionEstablished(string userId, string fromName, IRtcConnection connection)
    {
        SignalingStatus = $"Incoming connection request from {fromName}...";
        TrackConnection(userId, connection);
        LogStep("Incoming call received");
        NotifyStateChanged();
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

    private async Task CleanupPeerConnectionAsync(string userId)
    {
        await rtcConnector.CancelConnectionAsync(userId);

        if (_textChannels.TryRemove(userId, out var textChannel))
            await textChannel.DisposeAsync();

        if (_userConnections.TryRemove(userId, out var connection))
            connection.Dispose();

        contactsTracker.UpdateConnectionState(userId, RtcConnectionState.Closed);

        NotifyStateChanged();
    }

    private async Task AutoConnectFavoriteAsync(string userId)
    {
        try
        {
            await ConnectToUserAsync(userId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-connect to favourite {UserId} failed", userId);
        }
    }

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
            if (existing.IsDisposed)
            {
                var deadEntry = _textChannels.FirstOrDefault(x => x.Value == existing);
                if (!string.IsNullOrWhiteSpace(deadEntry.Key))
                {
                    _textChannels.TryRemove(deadEntry.Key, out _);
                }

                return null;
            }

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
            contactsTracker.UpdateConnectionState(userId, state);
            NotifyStateChanged();
        };

        contactsTracker.UpdateConnectionState(userId, connection.State);
        NotifyStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
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

public record Contact(string Id, string Name, DateTimeOffset LastSeen, RtcConnectionState ConnectionState, bool IsFavorite = false) : User(Id, Name);

public record CallInfo(string ConnectionId, string RemotePeerId, string RemotePeerName);

public record RtcTextMessage(string Text, bool IsSystem);

public record FavoriteContact(string Id, string Name);

public sealed record UserPresence(string UserId, string Name, DateTimeOffset LastSeen);

public sealed record PresencePayload(string Name);

public sealed record HungupPayload(string CallId);

public sealed record ConnectionRequestPayload(string ConnectionId, string FromName, WebRtcOffer Offer);

public sealed record AnswerPayload(string ConnectionId, WebRtcAnswer Answer);

public sealed record InitiateCallPayload(string ConnectionId);

public sealed record CallResponsePayload(string ConnectionId, bool Accepted);
     