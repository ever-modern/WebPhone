using EverModern.Blazor.DirectCommunication;
using Microsoft.JSInterop;
using WebPhone.Registration;

namespace WebPhone.Services;


public enum CallState
{
    New,
    Ringing,
    Active,
    Paused,
    Ended,
    Broken
}

public class CallAgent(
    WebRtcInterop webRtc,
    IMessagesChannel messagesChannel,
    IRtcConnection rtcConnection,
    Logger<CallAgent> logger)
{
    public event Action? StateChanged;
    public CallState CallState { get; private set; } = CallState.New;
    public string? SignalingStatus { get; private set; }
    public string? AudioStatusMessage { get; private set; }
    
    public async Task AcceptCallAsync()
    {
        string connectionId = rtcConnection.Id;
        
        NotifyStateChanged();

        await EnsureAudioAsync();
    }

    public async Task CancelCallAsync(bool notifyRemote = true)
    {
        SignalingStatus = "Call canceled.";
        NotifyStateChanged();
    }

    public Task DeclineIncomingCallAsync()
    {
        NotifyStateChanged();
        return Task.CompletedTask;
    }

    private async Task EnsureAudioAsync()
    {
        string? connectionId = rtcConnection.Id;
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

    private async Task PublishAsync(OutgoingMessage message)
        => await messagesChannel.Writer.WriteAsync(message);

    private void LogStep(string step)
        => logger.LogInformation("CallAgent step: {Step}", step);

    private void NotifyStateChanged() => StateChanged?.Invoke();
}
     