using EverModern.Blazor.DirectCommunication;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Primitives;
using Microsoft.JSInterop;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Text.Json;
using WebPhone.Registration;

namespace WebPhone.Services;

public record User(string Id, string Name);

public record UserConnection(User OtherUser, IRtcConnection RtcConnection);

public record OuterUser(string Id, string Name, DateTimeOffset LastSeen, RtcConnectionState ConnectionState) : User(Id, Name);

public record CallInfo(string ConnectionId, string RemotePeerId, string RemotePeerName);

public record RtcTextMessage(string Text, bool IsSystem);

public sealed class Phon(
    WebRtcInterop webRtc,
    IJSRuntime jsRuntime,
    ILogger<Phon> logger,
    PhoneOptions options,
    IMessagesChannel externalChannel,
    RtcConnector rtcConnector,
    BackendClient backendClient,
    User thisUser,
    EventCallback<IncomingMessage<ConnectionRequestPayload>> onIncomingCall) : IAsyncDisposable
{
    private readonly Stopwatch stepTimer = Stopwatch.StartNew();
    private readonly List<string> receivedMessages = [];
    private readonly int pollIntervalMs = Math.Max(options.PollIntervalMs, 250);
    private DateTimeOffset lastOutgoingTimestamp = DateTimeOffset.UtcNow;
    private long lastStepTimestamp;
    private PeriodicTimer? presenceTimer;
    private CancellationTokenSource? presenceCts;
    private Task? messageReaderTask;
    private CancellationTokenSource? messageReaderCts;

    public event Action? StateChanged;

    readonly ConcurrentDictionary<string, IRtcConnection> _userConnections = [];
    readonly ConcurrentDictionary<string, OuterUser> _presences = [];
    readonly ConcurrentDictionary<string, List<string>> _chats = [];

    public string DisplayName => thisUser.Name;

    public bool HasStoredProfileName { get; private set; }

    public string? SignalingStatus { get; private set; }

    public ElementReference RemoteAudio { get; set; }

    public string? AudioStatusMessage { get; private set; }

    public IncomingMessage<ConnectionRequestPayload>? CurrentCall { get; private set; }

    public IReadOnlyList<string> ReceivedMessages => receivedMessages;

    public IReadOnlyList<CallInfo> IncomingCalls { get; private set; } = [];

    public IReadOnlyList<OuterUser> Users => [.. _presences.Values];

    public async Task InitializeAsync()
    {
        await SubscribeForPushAsync(default).ContinueWith(t =>
        {
            if (t.IsFaulted)
                logger.LogWarning(t.Exception, "Push subscription failed");
            else
                logger.LogInformation("Push subscription successful");
        });

        StartMessageReader();
        StartPresenceLoop();
    }

    public Task<IRtcConnection> ConnectToUserAsync(string userId, CancellationToken cancellationToken)
        => rtcConnector.InitiateConnectionAsync(userId, thisUser.Name, cancellationToken);


    // Call control methods are now handled by CallAgent

    public async Task SendMessageAsync(string connectionId, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        await webRtc.SendMessageAsync(connectionId, message);
        receivedMessages.Add($"Me: {message}");
        NotifyStateChanged();
    }

    public async Task SubscribeForPushAsync(CancellationToken cancellationToken)
    {
        var resultJson = await jsRuntime.InvokeAsync<string>("registerPush", [Contract.VapidKeys.Public]);

        await backendClient.RegisterPushSubscriptionAsync(resultJson, cancellationToken);
    }

    public async Task<IBroadcastChannel<RtcTextMessage, RtcTextMessage>> StartTextingAsync(string userId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    private async Task EnsureAudioAsync(string connectionId)
    {
        LogStep("Starting audio capture");
        try
        {
            await webRtc.StartLocalStreamAsync(connectionId, new
            {
                audio = new
                {
                    echoCancellation = true,
                    noiseSuppression = true,
                    autoGainControl = true
                },
                video = false
            });
            await webRtc.AddLocalTracksAsync(connectionId);
            AudioStatusMessage = null;
            LogStep("Audio capture started");
        }
        catch (JSException ex)
        {
            AudioStatusMessage = ex.Message;
            LogStep("Audio capture failed");
            NotifyStateChanged();
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

    private async Task PublishAsync(OutgoingMessage message)
    {
        await externalChannel.Writer.WriteAsync(message);
        lastOutgoingTimestamp = DateTimeOffset.UtcNow;
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
                _ = rtcConnector.AcceptConnectionAsync(call.SenderClientId, thisUser.Name, call.Payload.Offer).ContinueWith(t =>
                {
                    _userConnections[call.SenderClientId] = t.Result;
                });
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
            logger.LogWarning("WebRTC step '{Step}' after {Elapsed}ms (+{Delta}ms)", step, elapsed, delta);
        }
        else
        {
            logger.LogInformation("WebRTC step '{Step}' after {Elapsed}ms (+{Delta}ms)", step, elapsed, delta);
        }
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

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
    }


}

public sealed record UserPresence(string UserId, string Name, DateTimeOffset LastSeen);

public sealed record PresencePayload(string Name);

public sealed record HungupPayload(string CallId);

public sealed record ConnectionRequestPayload(string ConnectionId, string FromName, WebRtcOffer Offer);

public sealed record AnswerPayload(string ConnectionId, WebRtcAnswer Answer);

public sealed record InitiateCallPayload(string ConnectionId);

public sealed record CallResponsePayload(string ConnectionId, bool Accepted);
     