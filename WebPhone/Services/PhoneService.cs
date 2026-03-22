using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using EverModern.Blazor.DirectCommunication;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using WebPhone.Registration;
using WebPhone.Registration.Pusher;

namespace WebPhone.Services;

public sealed class PhoneService(
    WebRtcService webRtc,
    IWebRtcConfigurator channels,
    HttpClient httpClient,
    IJSRuntime jsRuntime,
    ILogger<PhoneService> logger,
    IOptions<PhoneOptions> options,
    IOptions<PusherOptions> pusherOptions,
    IExternalChannel<Message> externalChannel) : IAsyncDisposable
{
    private const string PresenceAnnouncePath = "/api/announce-presence";
    private readonly Dictionary<string, UserPresence> activeUsers = new();
    private readonly Dictionary<string, string> contactNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch stepTimer = Stopwatch.StartNew();
    private readonly List<string> receivedMessages = [];
    private readonly int pollIntervalMs = Math.Max(options.Value.PollIntervalMs, 250);
    private readonly string presenceAnnounceUrl = BuildExternalUri(httpClient.BaseAddress, options.Value.ExternalChannelBaseUrl, PresenceAnnouncePath);
    private readonly PusherOptions pusherOptions = pusherOptions.Value;
    private readonly HttpClient httpClient = httpClient;
    private long lastStepTimestamp;
    private string connectionId = string.Empty;
    private bool isInitialized;
    private bool isAudioStarted;
    private bool isSignalingInitialized;
    private string userId = string.Empty;
    private string? currentSessionId;
    private string? currentPeerId;
    private string? currentPeerName;
    private bool isCallAccepted;
    private PeriodicTimer? presenceTimer;
    private CancellationTokenSource? presenceCts;
    private Task? messageReaderTask;
    private CancellationTokenSource? messageReaderCts;

    public event Action? StateChanged;

    public string DisplayName { get; set; } = string.Empty;

    public string PusherSecret { get; set; } = string.Empty;

    public bool HasStoredProfileName { get; private set; }

    public string? ProfileStatus { get; private set; }

    public string? SignalingStatus { get; private set; }

    public string? ConnectionState { get; private set; } = "new";

    public string? DataChannelState { get; private set; } = "new";

    public string MessageToSend { get; set; } = string.Empty;

    public bool CanSend => DataChannelState == "open";

    public ElementReference RemoteAudio { get; set; }

    public string? AudioStatusMessage { get; private set; }

    public CallRequestPayload? IncomingCall { get; private set; }

    public string? CurrentPeerId => currentPeerId;

    public string? CurrentPeerName => currentPeerName;

    public bool IsCallAccepted => isCallAccepted;

    public bool IsCalling => currentPeerId is not null && !isCallAccepted;

    public string GetContactName(string userId)
        => contactNames.TryGetValue(userId, out var name) ? name : string.Empty;

    public async Task EnsureContactNameAsync(string userId)
    {
        if (contactNames.ContainsKey(userId))
        {
            return;
        }

        var stored = await GetLocalStorageItemAsync(GetContactNameKey(userId));
        if (!string.IsNullOrWhiteSpace(stored))
        {
            contactNames[userId] = stored;
        }
    }

    public async Task SetContactNameAsync(string userId, string name)
    {
        contactNames[userId] = name ?? string.Empty;
        await SetLocalStorageItemAsync(GetContactNameKey(userId), contactNames[userId]);
        NotifyStateChanged();
    }

    public IReadOnlyList<string> ReceivedMessages => receivedMessages;

    public IEnumerable<UserPresence> AvailableUsers
        => activeUsers.Values
            .Where(user => user.UserId != userId)
            .OrderBy(user => user.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(user => user.UserId);

    public async Task InitializeAsync()
    {
        LogStep("Page initialized");
        webRtc.ConnectionStateChanged += HandleConnectionStateChanged;
        webRtc.DataChannelStateChanged += HandleDataChannelStateChanged;
        webRtc.DataMessageReceived += HandleDataMessageReceived;
        webRtc.RemoteStreamAvailable += HandleRemoteStreamAvailable;

        userId = await GetOrCreateUserIdAsync();
        DisplayName = await GetLocalStorageItemAsync("webrtc-user-name") ?? string.Empty;
        PusherSecret = await GetLocalStorageItemAsync("webrtc-pusher-secret") ?? string.Empty;
        HasStoredProfileName = !string.IsNullOrWhiteSpace(DisplayName);

        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            await SaveProfileAsync();
        }
    }

    private async Task EnsureInitializedAsync()
    {
        if (isInitialized)
        {
            return;
        }

        LogStep("Initializing WebRTC");
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            connectionId = Guid.NewGuid().ToString("N");
        }

        await webRtc.InitializeAsync(connectionId, [
            new WebRtcIceServer { Urls = ["stun:stun.relay.metered.ca:80"] },
            new WebRtcIceServer
            {
                Urls = ["turn:standard.relay.metered.ca:80"],
                Username = "ca04422b48d9f681eb1577de",
                Credential = "lJmBSxV942Wi2HEi"
            },
            new WebRtcIceServer
            {
                Urls = ["turn:standard.relay.metered.ca:80?transport=tcp"],
                Username = "ca04422b48d9f681eb1577de",
                Credential = "lJmBSxV942Wi2HEi"
            },
            new WebRtcIceServer
            {
                Urls = ["turn:standard.relay.metered.ca:443"],
                Username = "ca04422b48d9f681eb1577de",
                Credential = "lJmBSxV942Wi2HEi"
            },
            new WebRtcIceServer
            {
                Urls = ["turns:standard.relay.metered.ca:443?transport=tcp"],
                Username = "ca04422b48d9f681eb1577de",
                Credential = "lJmBSxV942Wi2HEi"
            }
        ]);
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

    public async Task ConnectToUserAsync(UserPresence user)
    {
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

        currentPeerId = user.UserId;
        currentPeerName = user.Name;
        currentSessionId = Guid.NewGuid().ToString("N");
        isCallAccepted = false;
        IncomingCall = null;
        await PrepareSessionAsync(currentSessionId);
        await EnsureAudioAsync();
        await PublishAsync(new SignalingMessage<CallRequestPayload>(MessageType.Call, new CallRequestPayload(userId, DisplayName, user.UserId, currentSessionId)));
        SignalingStatus = $"Calling {user.Name}...";
        LogStep("Call request sent");
        NotifyStateChanged();
    }

    public async Task AcceptIncomingCallAsync()
    {
        LogStep("Incoming call accepted");
        if (IncomingCall is null)
        {
            return;
        }

        currentPeerId = IncomingCall.FromUserId;
        currentPeerName = IncomingCall.FromName;
        currentSessionId = IncomingCall.SessionId;
        isCallAccepted = true;
        await PrepareSessionAsync(IncomingCall.SessionId);

        await PublishAsync(new SignalingMessage<CallAcceptPayload>(MessageType.Accept, new CallAcceptPayload(userId, DisplayName, IncomingCall.FromUserId, IncomingCall.SessionId)));

        SignalingStatus = $"Accepted call from {IncomingCall.FromName}.";
        LogStep("Call accept sent");
        IncomingCall = null;
        NotifyStateChanged();

        await EnsureAudioAsync();
    }

    public async Task CancelCallAsync()
    {
        if (string.IsNullOrWhiteSpace(currentPeerId))
        {
            return;
        }

        if (isInitialized)
        {
            await webRtc.CloseAsync(connectionId);
        }

        currentPeerId = null;
        currentPeerName = null;
        currentSessionId = null;
        isCallAccepted = false;
        isInitialized = false;
        isAudioStarted = false;
        SignalingStatus = "Call canceled.";
        NotifyStateChanged();
    }

    public Task DeclineIncomingCallAsync()
    {
        if (IncomingCall is null)
        {
            return Task.CompletedTask;
        }

        SignalingStatus = $"Declined call from {IncomingCall.FromName}.";
        IncomingCall = null;
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

    private void HandleConnectionStateChanged(object? sender, WebRtcConnectionStateChangedEventArgs args)
    {
        if (args.ConnectionId != connectionId)
        {
            return;
        }

        ConnectionState = args.State;
        NotifyStateChanged();
    }

    private void HandleDataChannelStateChanged(object? sender, WebRtcDataChannelStateChangedEventArgs args)
    {
        if (args.ConnectionId != connectionId)
        {
            return;
        }

        DataChannelState = args.State;
        NotifyStateChanged();
    }

    private void HandleDataMessageReceived(object? sender, WebRtcDataMessageEventArgs args)
    {
        if (args.ConnectionId != connectionId)
        {
            return;
        }

        receivedMessages.Add($"Remote: {args.Message}");
        NotifyStateChanged();
    }

    private async void HandleRemoteStreamAvailable(object? sender, WebRtcRemoteStreamEventArgs args)
    {
        if (args.ConnectionId != connectionId)
        {
            return;
        }

        await webRtc.AttachRemoteAudioAsync(connectionId, RemoteAudio);
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
        await channels.InitializeAsync(BuildChannelName(), BuildEventName());
        isSignalingInitialized = true;
        LogStep("Signaling initialized");
    }

    private string BuildChannelName()
        => "private-webrtc-lobby";

    private static string BuildEventName()
        => "client-signal";

    private async Task<string?> GetLocalStorageItemAsync(string key)
        => await jsRuntime.InvokeAsync<string?>("appInterop.getLocalStorageItem", key);

    private async Task SetLocalStorageItemAsync(string key, string value)
        => await jsRuntime.InvokeVoidAsync("appInterop.setLocalStorageItem", key, value);

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

        var payload = new PresenceAnnounceRequest(userId, DisplayName, DateTimeOffset.UtcNow);
        try
        {
            using var response = await httpClient.PostAsJsonAsync(presenceAnnounceUrl, payload);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Presence announce failed with status {StatusCode}", response.StatusCode);
                return;
            }

            var users = await response.Content.ReadFromJsonAsync<List<PresenceAnnounceResponse>>() ?? [];
            foreach (var presence in users)
            {
                if (string.Equals(presence.UserId, userId, StringComparison.Ordinal))
                {
                    continue;
                }

                activeUsers[presence.UserId] = new UserPresence(presence.UserId, presence.Name, presence.Timestamp);
            }

            PrunePresence();
            NotifyStateChanged();
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
            await SendPresenceAsync();
        }
    }

    private void StartPollingLoop()
    {
        return;
    }

    private async Task PublishAsync<TPayload>(SignalingMessage<TPayload> message)
    {
        var payload = JsonSerializer.SerializeToElement(message);
        await externalChannel.Writer.WriteAsync(new WebPhone.Registration.Message(MessageType.Signal, payload));
    }

    private void StartMessageReader()
    {
        messageReaderCts?.Cancel();
        messageReaderCts = new CancellationTokenSource();
        messageReaderTask = ReadMessagesAsync(messageReaderCts.Token);
    }

    private async Task ReadMessagesAsync(CancellationToken cancellationToken)
    {
        await foreach (var message in externalChannel.Reader.ReadAllAsync(cancellationToken))
        {
            if (message.Type != MessageType.ClientSignal && message.Type != MessageType.Signal)
            {
                continue;
            }

            var payload = JsonSerializer.Deserialize<WebPhone.Registration.Message>(message.Payload.GetRawText());
            if (payload is null)
            {
                continue;
            }

            await HandleSignalingPayloadAsync(payload);
        }
    }

    private async Task HandleSignalingPayloadAsync(WebPhone.Registration.Message message)
    {
        switch (message.Type)
        {
            case MessageType.Presence:
                var presence = message.Payload.Deserialize<PresencePayload>();
                if (presence is null || presence.UserId == userId)
                {
                    return;
                }

                activeUsers[presence.UserId] = new UserPresence(presence.UserId, presence.Name, presence.Timestamp);
                PrunePresence();
                LogStep($"Presence received from {presence.UserId}");
                NotifyStateChanged();
                break;
            case MessageType.Call:
                var call = message.Payload.Deserialize<CallRequestPayload>();
                if (call is null || call.ToUserId != userId)
                {
                    return;
                }

                IncomingCall = call;
                currentPeerName = call.FromName;
                SignalingStatus = $"Incoming call from {call.FromName}...";
                LogStep("Incoming call received");
                NotifyStateChanged();
                break;
            case MessageType.Accept:
                var accept = message.Payload.Deserialize<CallAcceptPayload>();
                if (accept is null || accept.ToUserId != userId)
                {
                    return;
                }

                if (currentSessionId != accept.SessionId)
                {
                    return;
                }

                isCallAccepted = true;
                await PrepareSessionAsync(accept.SessionId);
                await EnsureAudioAsync();
                await webRtc.CreateDataChannelAsync(connectionId, "chat");
                var offer = await webRtc.CreateOfferAsync(connectionId);
                await PublishAsync(new SignalingMessage<OfferPayload>(MessageType.Offer, new OfferPayload(userId, DisplayName, accept.FromUserId, accept.SessionId, offer)));
                SignalingStatus = $"Sending offer to {accept.FromName}...";
                LogStep("Offer sent");
                NotifyStateChanged();
                break;
            case MessageType.Offer:
                var offerPayload = message.Payload.Deserialize<OfferPayload>();
                if (offerPayload is null || offerPayload.ToUserId != userId)
                {
                    return;
                }

                if (!isCallAccepted)
                {
                    return;
                }

                currentPeerId = offerPayload.FromUserId;
                currentSessionId = offerPayload.SessionId;
                await PrepareSessionAsync(offerPayload.SessionId);
                await EnsureAudioAsync();
                await webRtc.SetRemoteDescriptionAsync(connectionId, offerPayload.Offer);
                var answer = await webRtc.CreateAnswerAsync(connectionId);
                await PublishAsync(new SignalingMessage<AnswerPayload>(MessageType.Answer, new AnswerPayload(userId, DisplayName, offerPayload.FromUserId, offerPayload.SessionId, answer)));
                SignalingStatus = $"Connected to {offerPayload.FromName}.";
                LogStep("Answer sent");
                NotifyStateChanged();
                break;
            case MessageType.Answer:
                var answerPayload = message.Payload.Deserialize<AnswerPayload>();
                if (answerPayload is null || answerPayload.ToUserId != userId)
                {
                    return;
                }

                if (currentSessionId != answerPayload.SessionId)
                {
                    return;
                }

                await webRtc.SetRemoteDescriptionAsync(connectionId, answerPayload.Answer);
                SignalingStatus = $"Connected to {answerPayload.FromName}.";
                LogStep("Answer received");
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

    private async Task PrepareSessionAsync(string sessionId)
    {
        LogStep($"Preparing session {sessionId}");
        if (connectionId == sessionId && isInitialized)
        {
            return;
        }

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
        webRtc.ConnectionStateChanged -= HandleConnectionStateChanged;
        webRtc.DataChannelStateChanged -= HandleDataChannelStateChanged;
        webRtc.DataMessageReceived -= HandleDataMessageReceived;
        webRtc.RemoteStreamAvailable -= HandleRemoteStreamAvailable;
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

    public sealed record PresencePayload(string UserId, string Name, DateTimeOffset Timestamp);

    public sealed record CallRequestPayload(string FromUserId, string FromName, string ToUserId, string SessionId);

    public sealed record CallAcceptPayload(string FromUserId, string FromName, string ToUserId, string SessionId);

    public sealed record OfferPayload(string FromUserId, string FromName, string ToUserId, string SessionId, WebRtcSessionDescription Offer);

    public sealed record AnswerPayload(string FromUserId, string FromName, string ToUserId, string SessionId, WebRtcSessionDescription Answer);

    private sealed record PresenceAnnounceRequest(string UserId, string Name, DateTimeOffset Timestamp);

    private sealed record PresenceAnnounceResponse(string UserId, string Name, DateTimeOffset Timestamp);

    private static string BuildExternalUri(Uri? appBaseAddress, string externalBaseUrl, string endpointPath)
    {
        if (appBaseAddress is null)
        {
            throw new InvalidOperationException("App base address is not configured.");
        }

        var baseUri = new Uri(appBaseAddress, externalBaseUrl);
        var endpointUri = new Uri(baseUri, endpointPath.TrimStart('/'));
        return endpointUri.ToString();
    }
}
