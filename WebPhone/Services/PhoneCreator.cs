using EverModern.Blazor.DirectCommunication;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using System.Data;
using System.Diagnostics;
using System.Text.Json;
using WebPhone.Registration;

namespace WebPhone.Services;

public class PhoneFactory(WebRtcInterop webRtc,
    IJSRuntime jsRuntime,
    ILoggerFactory loggerFactory,
    IOptions<PhoneOptions> options,
    IMessagesChannel externalChannel,
    RtcConnector rtcConnector)
{
    public Phone Create(User userInfo)
    {
        return new Phone(webRtc, jsRuntime, loggerFactory.CreateLogger<Phone>(), options.Value, externalChannel, rtcConnector, userInfo);
    }


public sealed class Phone(
    WebRtcInterop webRtc,
    IJSRuntime jsRuntime,
    ILogger<Phone> logger,
    PhoneOptions options,
    IMessagesChannel externalChannel,
    RtcConnector rtcConnector,
    User thisUser) : IAsyncDisposable
{
    const string BuildChannelName = "private-webrtc-lobby";

    const string BuildEventName = "client-signal";

    private readonly Dictionary<string, UserPresence> activeUsers = new();
    private readonly Dictionary<string, string> contactNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch stepTimer = Stopwatch.StartNew();
    private readonly List<string> receivedMessages = [];
    private readonly int pollIntervalMs = Math.Max(options.Value.PollIntervalMs, 250);
    private DateTimeOffset lastOutgoingTimestamp = DateTimeOffset.UtcNow;
    private long lastStepTimestamp;
    private bool isInitialized;
    private bool isAudioStarted;
    private bool isSignalingInitialized;
    private bool isCallAccepted;
    private PeriodicTimer? presenceTimer;
    private CancellationTokenSource? presenceCts;
    private Task? messageReaderTask;
    private CancellationTokenSource? messageReaderCts;

    public event Action? StateChanged;

    public string DisplayName { get; set; } = string.Empty;

    public bool HasStoredProfileName { get; private set; }

    public string? SignalingStatus { get; private set; }

    public bool CanSend => DataChannelState == "open";

    public ElementReference RemoteAudio { get; set; }

    public string? AudioStatusMessage { get; private set; }

    public IncomingMessage<CallRequestPayload>? CurrentCall { get; private set; }

    public bool IsCallAccepted => isCallAccepted;

    public bool IsCalling => currentPeerId is not null && !isCallAccepted;

    public string GetContactName(string userId)
        => contactNames.TryGetValue(userId, out var name) ? name : string.Empty;

    public IReadOnlyList<string> ReceivedMessages => receivedMessages;

    public IEnumerable<UserPresence> AvailableUsers
        => activeUsers.Values
            .Where(user => user.UserId != userId)
            .OrderBy(user => user.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(user => user.UserId);

    public async Task InitializeAsync()
    {
        try
        {
            await SubscribeForPushAsync(default);
            logger.LogInformation("Push subscription successful");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Push subscription failed");
        }


        userId = await GetOrCreateUserIdAsync();
        DisplayName = await GetLocalStorageItemAsync("webrtc-user-name") ?? string.Empty;
        HasStoredProfileName = !string.IsNullOrWhiteSpace(DisplayName);

        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            await SaveProfileAsync();
        }


        LogStep("Phose service initialized");
    }

    private async Task EnsureInitializedAsync()
    {
        if (isInitialized)
        {
            return;
        }

        LogStep("Initializing WebRTC");


        await webRtc.InitializeAsync(connectionId, options.Value.WebRtcIceServers);
        isInitialized = true;
        LogStep("WebRTC initialized");
    }

    public async Task SaveProfileAsync()
    {
        LogStep("Saving profile");
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ProfileStatus = "Name is required.";
            NotifyStateChanged();
            return;
        }

        await SetLocalStorageItemAsync("webrtc-user-name", DisplayName);
        await SetLocalStorageItemAsync("webrtc-pusher-secret", PusherSecret);
        HasStoredProfileName = true;
        ProfileStatus = "Profile saved.";
        await EnsureSignalingAsync();
        await SendPresenceAsync();
        StartPresenceLoop();
        StartMessageReader();
        LogStep("Profile saved and presence started");
        NotifyStateChanged();
    }

    public async Task<RtcConnection?> ConnectToUserAsync(string userId, CancellationToken cancellationToken)
    {
        if (activeUsers.TryGetValue(userId, out var user) is false)
        {
            return null;
        }

        var connection = await rtcConnector.InitiateConnectionAsync(user.UserId, thisUser.Name, cancellationToken: cancellationToken);

        LogStep($"Connect requested for {user.UserId}");
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            ProfileStatus = "Set your name first.";
            NotifyStateChanged();
            return;
        }

        if (!string.IsNullOrWhiteSpace(currentPeerId) && !string.Equals(currentPeerId, user.UserId, StringComparison.Ordinal))
        {
            await CancelCallAsync();
        }

        isCallAccepted = false;
        CurrentCall = null;
        await PrepareSessionAsync(currentSessionId);
        await EnsureAudioAsync();
        await PublishAsync(new OutgoingMessage(MessageType.ConnectionAttempt, JsonSerializer.SerializeToElement(new CallRequestPayload(DisplayName)), user.UserId));
        SignalingStatus = $"Calling {user.Name}...";
        LogStep("Call request sent");
        NotifyStateChanged();
    }

    public async Task AcceptIncomingCallAsync()
    {
        LogStep("Incoming call accepted");
        if (CurrentCall is null)
        {
            return;
        }


        isCallAccepted = true;
        await PrepareSessionAsync(CurrentCall.SessionId);

        await PublishAsync(new SignalingMessage<CallAcceptPayload>(
            MessageType.RtcAccept, new CallAcceptPayload(userId, DisplayName, CurrentCall.FromUserId, CurrentCall.SessionId)));

        SignalingStatus = $"Accepted call from {CurrentCall.FromName}.";
        LogStep("Call accept sent");
        CurrentCall = null;
        NotifyStateChanged();

        await EnsureAudioAsync();
    }

    public Task CancelCallAsync()
        => CancelCallAsync(notifyRemote: true);

    public async Task CancelCallAsync(bool notifyRemote = true)
    {
        if (string.IsNullOrWhiteSpace(currentPeerId))
        {
            return;
        }

        if (isInitialized)
        {
            await webRtc.CloseAsync(connectionId);
        }

        if (notifyRemote && !string.IsNullOrWhiteSpace(currentPeerId))
        {
            // notify remote side about hangup using typed payload
            await PublishAsync(new SignalingMessage<HungupPayload>(MessageType.Hangup, new HangupPayload(userId, currentPeerId)));
        }

        isCallAccepted = false;
        isInitialized = false;
        isAudioStarted = false;
        SignalingStatus = "Call canceled.";
        NotifyStateChanged();
    }

    public Task DeclineIncomingCallAsync()
    {
        if (CurrentCall is null)
        {
            return Task.CompletedTask;
        }

        SignalingStatus = $"Declined call from {CurrentCall.FromName}.";
        CurrentCall = null;
        NotifyStateChanged();
        return Task.CompletedTask;
    }

    public async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(MessageToSend))
        {
            return;
        }

        await webRtc.SendMessageAsync(connectionId, MessageToSend);
        receivedMessages.Add($"Me: {MessageToSend}");
        MessageToSend = string.Empty;
        NotifyStateChanged();
    }

    public async Task SubscribeForPushAsync(CancellationToken cancellationToken)
    {
        var resultJson = await jsRuntime.InvokeAsync<string>("registerPush", [Contract.VapidKeys.Public]);

        var obj = JsonSerializer.Deserialize<object>(resultJson);

        await backendClient.RegisterPushSubscriptionAsync(resultJson, cancellationToken);
    }

    private async Task EnsureAudioAsync()
    {
        if (isAudioStarted)
        {
            return;
        }

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
            isAudioStarted = true;
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

    private async Task EnsureSignalingAsync()
    {
        if (isSignalingInitialized)
        {
            return;
        }

        LogStep("Initializing signaling");
        await channels.ConfigureAsync(new ChannelsConfiguration(PusherSecret));
        await channels.InitializeAsync(BuildChannelName, BuildEventName);
        isSignalingInitialized = true;
        LogStep("Signaling initialized");
    }




    private static string GetContactNameKey(string userId)
        => $"webrtc-contact-name-{userId}";

    private async Task<string> GetOrCreateUserIdAsync()
    {
        var stored = await GetLocalStorageItemAsync("webrtc-user-id");
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return stored;
        }

        var newId = Guid.NewGuid().ToString("N");
        await SetLocalStorageItemAsync("webrtc-user-id", newId);
        return newId;
    }

    private async Task SendPresenceAsync()
    {
        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            return;
        }

        var payload = JsonSerializer.SerializeToElement(new PresencePayload(DisplayName));
        try
        {
            await externalChannel.Writer.WriteAsync(new WebPhone.Registration.OutgoingMessage(MessageType.Presence, payload, null));
            lastOutgoingTimestamp = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Presence announce failed");
        }
    }

    private void StartPresenceLoop()
    {
        presenceCts?.Cancel();
        presenceCts = new CancellationTokenSource();
        presenceTimer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        _ = RunPresenceLoopAsync(presenceCts.Token);
    }

    private async Task RunPresenceLoopAsync(CancellationToken cancellationToken)
    {
        if (presenceTimer is null)
        {
            return;
        }

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
            if (message.Type == MessageType.Presence)
            {
                await HandleSignalingPayloadAsync(message);
                continue;
            }

            if (message.Type != MessageType.ClientSignal && message.Type != MessageType.Signal)
            {
                continue;
            }

            var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
            var payload = JsonSerializer.Deserialize<IncomingMessage>(message.Payload, opts);
            if (payload is null)
            {
                continue;
            }

            await HandleSignalingPayloadAsync(payload);
        }
    }

    private async Task HandleSignalingPayloadAsync(IncomingMessage message)
    {
        switch (message.Type)
        {
            case MessageType.Presence:
                var presence = message.SpecifyPayload<PresencePayload>();
                activeUsers[presence.SenderClientId] = new UserPresence(presence.SenderClientId, presence.Payload.Name, presence.DateTime);
                PrunePresence();
                LogStep($"Presence received from {presence.SenderClientId}");
                NotifyStateChanged();
                break;
            case MessageType.ConnectionAttempt:
                var call = message.SpecifyPayload<CallRequestPayload>();
                if (call is null)
                    break;
                CurrentCall = call;
                SignalingStatus = $"Incoming call from {call.Payload.FromName}...";
                LogStep("Incoming call received");
                NotifyStateChanged();
                break;
            case MessageType.RtcAccept:
                var accept = message.SpecifyPayload<CallAcceptPayload>();
                isCallAccepted = true;
                await PrepareSessionAsync();
                await EnsureAudioAsync();
                await webRtc.CreateDataChannelAsync(connectionId, "chat");
                var offer = await webRtc.CreateOfferAsync(connectionId);
                await PublishAsync(new SignalingMessage<OfferPayload>(MessageType.RtcOffer, new OfferPayload(userId, DisplayName, accept.FromUserId, accept.SessionId, offer)));
                SignalingStatus = $"Sending offer to {accept.FromName}...";
                LogStep("Offer sent");
                NotifyStateChanged();
                break;
            case MessageType.RtcOffer:
                var offerPayload = message.SpecifyPayload<OfferPayload>();
                await PrepareSessionAsync(offerPayload.SessionId);
                await EnsureAudioAsync();
                await webRtc.SetRemoteDescriptionAsync(connectionId, offerPayload.Offer);
                var answer = await webRtc.CreateAnswerAsync(connectionId);
                await PublishAsync(new SignalingMessage<AnswerPayload>(MessageType.RtcAnswer, new AnswerPayload(userId, DisplayName, offerPayload.FromUserId, offerPayload.SessionId, answer)));
                SignalingStatus = $"Connected to {offerPayload.FromName}.";
                LogStep("Answer sent");
                NotifyStateChanged();
                break;
            case MessageType.RtcAnswer:
                var answerIncoming = message.SpecifyPayload<AnswerPayload>();
                await webRtc.SetRemoteDescriptionAsync(connectionId, answerIncoming.Answer);
                SignalingStatus = $"Connected to {answerIncoming.FromName}.";
                LogStep("Answer received");
                NotifyStateChanged();
                break;
            case MessageType.Hangup:
                var hangup = message.SpecifyPayload<HungupPayload>();
                if (hangup is null || hangup.Payload.CallId != CurrentCall.Payload.ConnectionId)
                {
                    return;
                }

                await CancelCallAsync(notifyRemote: false);
                LogStep("Remote hangup received");
                NotifyStateChanged();
                break;
        }
    }

    private void PrunePresence()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-30);
        var staleUsers = activeUsers
            .Where(pair => pair.Value.LastSeen < cutoff)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var user in staleUsers)
        {
            activeUsers.Remove(user);
        }
    }

    private async Task PrepareSessionAsync()
    {
        LogStep($"Preparing session");

        if (isInitialized)
        {
            await webRtc.CloseAsync(connectionId);
        }

        connectionId = sessionId;
        isInitialized = false;
        isAudioStarted = false;
        await EnsureInitializedAsync();
        LogStep($"Session prepared {sessionId}");
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

        if (isInitialized)
        {
            await webRtc.CloseAsync(connectionId);
        }
    }

    public sealed record UserPresence(string UserId, string Name, DateTimeOffset LastSeen);

    public sealed record PresencePayload(string Name);

    public sealed record HungupPayload(string CallId);

    public sealed record CallRequestPayload(string ConnectionId, string FromName);

    public sealed record CallAcceptPayload(string ConnectionId, string FromName, WebRtcSessionDescription Offer);

    public sealed record AnswerPayload(WebRtcSessionDescription Answer);
}
