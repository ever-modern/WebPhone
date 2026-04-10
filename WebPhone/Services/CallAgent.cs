using System.Text.Json;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace WebPhone.Services;

public enum CallState
{
    New,
    Ringing,
    Active,
    Paused,
    Ended,
    Broken,
}

public class CallAgent
{
    private const string CallPingMessage = "call:ping";

    private readonly WebRtcInterop webRtc;
    private readonly IMessagesChannel messagesChannel;
    private readonly RtcMessageChannel textChannel;
    private readonly IRtcConnection rtcConnection;
    private readonly ILogger<CallAgent> logger;
    private CancellationTokenSource? pingLoopCts;
    private Task? pingLoopTask;
    private ElementReference remoteAudioElement;
    private bool hasRemoteAudioElement;

    readonly EventSource _stateChanged = new();
    public INotifier StateChanged => _stateChanged;
    public CallState CallState { get; private set; } = CallState.New;
    public string? SignalingStatus { get; private set; }
    public string? AudioStatusMessage { get; private set; }

    public CallAgent(
        WebRtcInterop webRtc,
        IMessagesChannel messagesChannel,
        RtcMessageChannel textChannel,
        IRtcConnection rtcConnection,
        ILogger<CallAgent> logger
    )
    {
        this.webRtc = webRtc;
        this.messagesChannel = messagesChannel;
        this.textChannel = textChannel;
        this.rtcConnection = rtcConnection;
        this.logger = logger;
        rtcConnection.StateChanged += OnRtcStateChanged;
        webRtc.RemoteStreamAvailable += OnRemoteStreamAvailable;
    }

    public async Task StartOutgoingCallAsync(
        ElementReference remoteAudioElement,
        CancellationToken cancellationToken = default
    )
    {
        if (CallState is CallState.Ended or CallState.Broken)
        {
            return;
        }

        CallState = CallState.Ringing;
        SignalingStatus = "Starting call...";
        NotifyStateChanged();

        await EnsureAudioAsync();
        this.remoteAudioElement = remoteAudioElement;
        hasRemoteAudioElement = true;
        await TryAttachRemoteAudioAsync();

        pingLoopCts?.Cancel();
        pingLoopCts?.Dispose();
        pingLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pingLoopTask = RunCallPingLoopAsync(pingLoopCts.Token);

        SignalingStatus = "Calling...";
        NotifyStateChanged();
    }

    public async Task AcceptCallAsync(ElementReference remoteAudioElement)
    {
        if (CallState is CallState.Ended or CallState.Broken)
        {
            return;
        }

        CallState = CallState.Ringing;
        SignalingStatus = "Accepting call...";
        NotifyStateChanged();

        await EnsureAudioAsync();
        this.remoteAudioElement = remoteAudioElement;
        hasRemoteAudioElement = true;
        await TryAttachRemoteAudioAsync();

        pingLoopCts?.Cancel();
        pingLoopCts?.Dispose();
        pingLoopCts = null;

        if (pingLoopTask is not null)
        {
            try
            {
                await pingLoopTask;
            }
            catch { }
        }

        await textChannel.Writer.WriteAsync(new RtcTextMessage("call:accepted", true));

        if (string.IsNullOrWhiteSpace(AudioStatusMessage))
        {
            CallState = CallState.Active;
            SignalingStatus = "Call is active.";
        }
        else
        {
            CallState = CallState.Broken;
            SignalingStatus = "Call failed to start audio.";
        }

        NotifyStateChanged();
    }

    public async Task CancelCallAsync(bool notifyRemote = true)
    {
        if (CallState is CallState.Ended)
        {
            return;
        }

        pingLoopCts?.Cancel();
        pingLoopCts?.Dispose();
        pingLoopCts = null;

        if (pingLoopTask is not null)
        {
            try
            {
                await pingLoopTask;
            }
            catch { }
        }

        if (notifyRemote)
        {
            await PublishAsync(
                new OutgoingMessage(
                    MessageType.Hangup,
                    JsonSerializer.SerializeToElement(new HungupPayload(rtcConnection.Id)),
                    rtcConnection.RemotePeer
                )
            );
        }

        CallState = CallState.Ended;
        SignalingStatus = "Call canceled.";
        NotifyStateChanged();
    }

    public async Task DeclineIncomingCallAsync()
    {
        await PublishAsync(
            new OutgoingMessage(
                MessageType.CallResponse,
                JsonSerializer.SerializeToElement(new CallResponsePayload(rtcConnection.Id, false)),
                rtcConnection.RemotePeer
            )
        );

        CallState = CallState.Ended;
        SignalingStatus = "Incoming call declined.";
        NotifyStateChanged();
    }

    private async Task EnsureAudioAsync()
    {
        string? connectionId = rtcConnection.Id;
        LogStep("Starting audio capture");
        logger.LogInformation("[AUDIO] Starting audio capture for connection {ConnectionId}", connectionId);
        try
        {
            await webRtc.StartLocalStreamAsync(
                connectionId,
                new
                {
                    audio = new
                    {
                        echoCancellation = true,
                        noiseSuppression = true,
                        autoGainControl = true,
                    },
                    video = false,
                }
            );
            logger.LogInformation("[AUDIO] Local stream started for connection {ConnectionId}", connectionId);

            await webRtc.AddLocalTracksAsync(connectionId);
            logger.LogInformation("[AUDIO] Local tracks added to connection {ConnectionId}", connectionId);

            AudioStatusMessage = null;
            LogStep("Audio capture started");
        }
        catch (JSException ex)
        {
            AudioStatusMessage = ex.Message;
            logger.LogError(ex, "[AUDIO] Audio capture failed for connection {ConnectionId}", connectionId);
            LogStep("Audio capture failed");
            NotifyStateChanged();
        }
    }

    private void OnRtcStateChanged(RtcConnectionState state)
    {
        if (
            state
            is RtcConnectionState.Failed
                or RtcConnectionState.Disconnected
                or RtcConnectionState.Closed
        )
        {
            CallState = CallState.Broken;
            SignalingStatus = "Connection dropped.";
            NotifyStateChanged();
        }
        logger.LogInformation($"Connection {rtcConnection.Id} now has {state} state.");
    }

    private async Task PublishAsync(OutgoingMessage message) =>
        await messagesChannel.Writer.WriteAsync(message);

    private async Task TryAttachRemoteAudioAsync()
    {
        if (!hasRemoteAudioElement)
        {
            return;
        }

        try
        {
            await webRtc.AttachRemoteAudioAsync(rtcConnection.Id, remoteAudioElement);
        }
        catch (JSException) { }
    }

    private void OnRemoteStreamAvailable(object? sender, WebRtcRemoteStreamEventArgs e)
    {
        if (e.ConnectionId != rtcConnection.Id)
        {
            return;
        }

        _ = TryAttachRemoteAudioAsync();
    }

    private async Task RunCallPingLoopAsync(CancellationToken cancellationToken)
    {
        using var pingTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(900));
        while (await pingTimer.WaitForNextTickAsync(cancellationToken))
        {
            await textChannel.Writer.WriteAsync(
                new RtcTextMessage(CallPingMessage, true),
                cancellationToken
            );
        }
    }

    public async Task AttachRemoteAudioAsync(ElementReference remoteAudioElement)
    {
        this.remoteAudioElement = remoteAudioElement;
        hasRemoteAudioElement = true;
        await TryAttachRemoteAudioAsync();
    }

    private void LogStep(string step) => logger.LogInformation("CallAgent step: {Step}", step);

    private void NotifyStateChanged() => _stateChanged.Invoke();
}
