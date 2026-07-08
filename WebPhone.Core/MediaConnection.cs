using System.Text.Json;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using EverModern.Threading.Locks;
using Microsoft.Extensions.Logging;
using WebPhone.Channels;

namespace WebPhone;

public class MediaConnection(UnifiedRtcConnection connection, ILogger logger) : IDisposable
{
    readonly Lock _locker = new();

    bool _disposed;

    Subscription? _reading;

    readonly ObservedValue<InteractionState> _innerState = new(
        connection.State.Value == RtcConnectionState.Connected
            ? InteractionState.Connected.Instance
            : InteractionState.Disconnected.Instance
    );

    public IValueNotifier<InteractionState> State => _innerState;

    readonly RtcConnectionMessageChannel channel = new(
        connection.Bytes
    );

    public MediaConnection Started()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        channel.Received.Subscribe(ReceiveHandler);

        _ = Task.Run(SenderLoop);

        return this;
    }

    public async ValueTask AcceptCall()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MediaState mediaState;
        using (var _ = _locker.LockScope())
        {
            if (_innerState.Value is not InteractionState.ReceivingCall receivingCall)
                return;

            var audio = receivingCall.Audio
                ? new MediaPartState(true, true)
                : new MediaPartState(false, false);
            var video = receivingCall.Video
                ? new MediaPartState(true, true)
                : new MediaPartState(false, false);

            mediaState = new(audio, video);

            await channel.WriteAsync(
                RtcMessage.Create(
                    RtcMessageType.WantCall,
                    new InteractionState.Calling
                    {
                        Id = receivingCall.Id,
                        Audio = receivingCall.Audio,
                        Video = receivingCall.Video,
                    }
                )
            );

            _innerState.Change(new InteractionState.OnCall { MediaState = mediaState });
        }

        await connection.SetMediaStateAsync(mediaState);
    }

    public async ValueTask RejectCall()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var _ = _locker.LockScope();
        if (_innerState.Value is not InteractionState.ReceivingCall receivingCall)
            return;
        await channel.WriteAsync(RtcMessage.Create(RtcMessageType.RejectCall, receivingCall.Id));
        _innerState.Change(InteractionState.Connected.Instance);
    }

    public async ValueTask Call(bool audio = true, bool video = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var _ = _locker.LockScope();
        if (_innerState.Value.GetType() != typeof(InteractionState.Connected))
            return;
        _innerState.Change(new InteractionState.Calling { Audio = audio, Video = video });
    }

    public async ValueTask StopCalling()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var _ = _locker.LockScope();
        if (_innerState.Value is not InteractionState.Calling calling)
            return;
        await channel.WriteAsync(RtcMessage.Create(RtcMessageType.RejectCall, calling.Id));
        _innerState.Change(InteractionState.Connected.Instance);
    }

    public async ValueTask StopCall()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var _ = _locker.LockScope();
        if (_innerState.Value is not InteractionState.OnCall)
            return;
        await channel.WriteAsync(new RtcMessage(RtcMessageType.StopCall));
        _innerState.Change(InteractionState.Connected.Instance);
    }

    async Task SyncMediaAsync(MediaState mediaState)
    {
        try
        {
            await connection.SetMediaStateAsync(mediaState);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sync media state");
        }
    }

    static readonly MediaState MediaDisabled = new(
        new MediaPartState(false, false),
        new MediaPartState(false, false)
    );

    public void Dispose()
    {
        _disposed = true;
        _reading?.Dispose();
    }

    void ReceiveHandler(RtcMessage message, Subscription sub)
    {
        try
        {
            _reading = sub;
            
            if (message.Type is RtcMessageType.Disconnect)
            {
                logger.LogInformation(
                    "Received disconnect ping from the other end. Closing connection."
                );

                _ = channel.WriteAsync(new RtcMessage(RtcMessageType.Disconnect));

                _innerState.Change(InteractionState.Disconnected.Instance);

                sub.Dispose();

                return;
            }

            if (message.Type is RtcMessageType.WantCall)
            {
                var payload = JsonSerializer.Deserialize<InteractionState.Calling>(message.Payload);

                if (payload is null)
                    return;

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

                    var mediaState = new MediaState(audio, video);
                    _innerState.Change(
                        new InteractionState.OnCall { MediaState = mediaState }
                    );
                    _ = SyncMediaAsync(mediaState);
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
                    _ = SyncMediaAsync(MediaDisabled);
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
            while (_disposed is false)
            {
                await Task.Delay(500);

                using var _ = _locker.LockScope();

                if (_innerState.Value is InteractionState.Calling calling)
                {
                    logger.LogInformation("Sending WantCall message");
                    await channel.WriteAsync(RtcMessage.Create(RtcMessageType.WantCall, calling));
                }

                await channel.WriteAsync(new RtcMessage(RtcMessageType.Ping));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Media sender failed");
        }
    }
}
