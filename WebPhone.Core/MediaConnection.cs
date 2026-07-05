using System.Text.Json;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using EverModern.Threading.Channels;
using EverModern.Threading.Locks;
using Microsoft.Extensions.Logging;
using WebPhone.Channels;
using WebPhone.Domain;

namespace WebPhone;

public class MediaConnection(UnifiedRtcConnection connection, ILogger logger) : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _locker = new(1, 1);

    bool _disposed;

    readonly ObservedValue<InteractionState> _innerState = new(
        connection.State.Value == RtcConnectionState.Connected
            ? InteractionState.Connected.Instance
            : InteractionState.Disconnected.Instance
    );

    public IValueNotifier<InteractionState> State => _innerState;

    readonly IBroadcastChannel<RtcMessage, RtcMessage> channel = new RtcConnectionMessageChannel(
        connection
    );

    public MediaConnection Started()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(SenderLoop);

        return this;
    }

    public async ValueTask AcceptCall()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var _ = await _locker.LockScopeAsync();
        if (_innerState.Value is not InteractionState.ReceivingCall receivingCall)
            return;
        _innerState.Change(
            new InteractionState.Calling
            {
                Id = receivingCall.Id,
                Audio = receivingCall.Audio,
                Video = receivingCall.Video,
            }
        );
    }

    public async ValueTask RejectCall()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var _ = await _locker.LockScopeAsync();
        if (_innerState.Value is not InteractionState.ReceivingCall receivingCall)
            return;
        channel.Writer.TryWrite(RtcMessage.Create(RtcMessageType.RejectCall, receivingCall.Id));
        _innerState.Change(InteractionState.Connected.Instance);
    }

    public async ValueTask Call(bool audio = true, bool video = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var _ = await _locker.LockScopeAsync();
        if (_innerState.Value.GetType() != typeof(InteractionState.Connected))
            return;
        _innerState.Change(new InteractionState.Calling { Audio = audio, Video = video });
    }

    public async ValueTask StopCalling()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var _ = await _locker.LockScopeAsync();
        if (_innerState.Value is not InteractionState.Calling calling)
            return;
        channel.Writer.TryWrite(RtcMessage.Create(RtcMessageType.RejectCall, calling.Id));
        _innerState.Change(InteractionState.Connected.Instance);
    }

    public async ValueTask StopCall()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var _ = await _locker.LockScopeAsync();
        if (_innerState.Value is not InteractionState.OnCall)
            return;
        channel.Writer.TryWrite(new RtcMessage(RtcMessageType.StopCall));
        _innerState.Change(InteractionState.Connected.Instance);
    }

    public void Dispose()
    {
        _disposed = true;
        _cts.Cancel();
    }

    async Task ReceiveLoop()
    {
        try
        {
            using var messages = channel.Subscribe(_ => true);

            await foreach (var message in messages.ReadAllAsync(_cts.Token))
            {
                using var _ = await _locker.LockScopeAsync(_cts.Token);

                if (message.Type is RtcMessageType.Disconnect)
                {
                    logger.LogInformation(
                        "Received disconnect ping from the other end. Closing connection."
                    );

                    channel.Writer.TryWrite(new RtcMessage(RtcMessageType.Disconnect));

                    _innerState.Change(InteractionState.Disconnected.Instance);

                    await _cts.CancelAsync();

                    return;
                }

                if (message.Type is RtcMessageType.WantCall)
                {
                    var payload = JsonSerializer.Deserialize<InteractionState.Calling>(
                        message.Payload
                    );

                    if (payload is null)
                        continue;

                    if (
                        _innerState.Value is InteractionState.Calling calling
                        && calling.Id == payload.Id
                    )
                    {
                        var audio = payload.Audio
                            ? new MediaPartState(true, true)
                            : new MediaPartState(false, false);

                        var video = payload.Video
                            ? new MediaPartState(true, true)
                            : new MediaPartState(false, false);

                        _innerState.Change(
                            new InteractionState.OnCall { MediaState = new(audio, video) }
                        );
                    }
                    else if (_innerState.Value is InteractionState.Calling)
                    {
                        // Already sent ack, ignore further WantCall from caller
                    }
                    else if (_innerState.Value is not InteractionState.ReceivingCall)
                    {
                        _innerState.Change(
                            new InteractionState.ReceivingCall
                            {
                                Id = payload.Id,
                                Audio = payload.Audio,
                                Video = payload.Video,
                            }
                        );
                    }
                }
                else if (message.Type is RtcMessageType.RejectCall)
                {
                    var rejectId = JsonSerializer.Deserialize<long>(message.Payload);
                    if (
                        (
                            _innerState.Value is InteractionState.Calling calling
                            && calling.Id == rejectId
                        )
                        || (
                            _innerState.Value is InteractionState.ReceivingCall receivingCall
                            && receivingCall.Id == rejectId
                        )
                    )
                    {
                        _innerState.Change(InteractionState.Connected.Instance);
                    }
                }
                else if (message.Type is RtcMessageType.StopCall)
                {
                    if (_innerState.Value is InteractionState.OnCall)
                    {
                        _innerState.Change(InteractionState.Connected.Instance);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Media receiver failed");
        }
    }

    async Task SenderLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(100, _cts.Token);

                using var _ = await _locker.LockScopeAsync(_cts.Token);

                if (_innerState.Value is InteractionState.Calling calling)
                {
                    channel.Writer.TryWrite(RtcMessage.Create(RtcMessageType.WantCall, calling));
                }

                channel.Writer.TryWrite(new RtcMessage(RtcMessageType.Ping));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Media sender failed");
        }
    }
}
