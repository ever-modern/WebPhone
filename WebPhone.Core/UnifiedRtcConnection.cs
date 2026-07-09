using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using Microsoft.AspNetCore.Components;

namespace WebPhone;

public class UnifiedRtcConnection(
    Func<string> getId,
    Func<ValueTask> dispose,
    IValueNotifier<RtcConnectionState> state,
    BytesChannel bytesChannel,
    Func<Task<MediaState>> getMediaState,
    Func<MediaState, Task> setMediaState,
    Func<ElementReference, Task> setVideoTarget,
    Func<ElementReference, Task> setLocalVideoTarget
)
    : DelegatedRtcConnection(
        getId,
        dispose,
        state,
        bytesChannel,
        getMediaState,
        setMediaState,
        setVideoTarget,
        setLocalVideoTarget
    ) { }

public static class ConnectionProxyExtensions
{
    static readonly MediaState _defaultMediaState = new(new(false, false), new(false, false));

    public static UnifiedRtcConnection GetUnifiedConnection(
        this PeerConnectionsDispatcher dispatcher,
        string peerId
    )
    {
        ElementReference? videoRemoteTarget = null;
        ElementReference? localVideoTarget = null;

        MediaState mediaState = _defaultMediaState;

        ObservedValue<IRtcConnection?> connection = new(value: null);
        ObservedValue<RtcConnectionState> state = new(
            value: connection.Value?.State.Value ?? RtcConnectionState.Disconnected
        );
        EventSource<byte[]> bytesReceived = new();
        Subscription? bytesSub = null;
        IDisposable? rtcStateSub = null;

        async void ApplyTargetsAndMedia(IRtcConnection? conn)
        {
            if (conn is null) return;

            try
            {
                if (videoRemoteTarget is not null)
                    await conn.SetVideoTargetAsync(videoElement: videoRemoteTarget.Value);
                if (localVideoTarget is not null)
                    await conn.SetLocalVideoTargetAsync(videoElement: localVideoTarget.Value);

                await conn.SetMediaStateAsync(mediaState: mediaState);
            }
            catch (ObjectDisposedException) { }
        }

        BytesChannel bytesChannel = new(
            (bytes) =>
                connection.Value?.Bytes.WriteAsync(bytes) ?? ValueTask.FromResult(result: false),
            bytesReceived
        );
        var connectionChangedSub = dispatcher.ConnectionsChange.SubscribeAfter(() =>
        {
            var newConnection = dispatcher.FindReadyConnection(peerId: peerId);

            // Track real RTC state by subscribing to the underlying connection.
            rtcStateSub?.Dispose();
            if (newConnection is not null)
            {
                state.Change(newValue: newConnection.State.Value);
                rtcStateSub = newConnection.State.Subscribe(s =>
                    state.Change(newValue: s));
            }
            else
            {
                state.Change(newValue: RtcConnectionState.Disconnected);
                rtcStateSub = null;
            }

            if (newConnection == connection.Value)
                return;

            bytesSub?.Dispose();
            bytesSub = newConnection?.Bytes?.Received.Subscribe(handler: bytesReceived.Invoke);

            connection.Change(newValue: newConnection);

            ApplyTargetsAndMedia(newConnection);
        });

        var initialConnection = dispatcher.FindReadyConnection(peerId: peerId);
        connection.Change(newValue: initialConnection);

        if (initialConnection is not null)
        {
            state.Change(newValue: initialConnection.State.Value);
            rtcStateSub = initialConnection.State.Subscribe(s =>
                state.Change(newValue: s));
            bytesSub = initialConnection.Bytes.Received.Subscribe(handler: bytesReceived.Invoke);
        }

        Action dispose = () =>
        {
            rtcStateSub?.Dispose();
            connection.Dispose();
            bytesSub?.Dispose();
            connectionChangedSub.Dispose();
            state.Dispose();
        };

        UnifiedRtcConnection result = new(
            getId: () => connection.Value?.Id ?? "undefined",
            dispose: () =>
            {
                dispose();
                return ValueTask.CompletedTask;
            },
            state: state,
            bytesChannel: bytesChannel,
            getMediaState: async () =>
            {
                if (connection.Value is null)
                    return new(
                        Audio: new(InputEnabled: false, OutputEnabled: false),
                        Video: new(InputEnabled: false, OutputEnabled: false)
                    );
                try
                {
                    return await connection.Value.GetMediaStateAsync();
                }
                catch (ObjectDisposedException)
                {
                    return new(
                        Audio: new(InputEnabled: false, OutputEnabled: false),
                        Video: new(InputEnabled: false, OutputEnabled: false)
                    );
                }
            },
            setMediaState: async newMediaState =>
            {
                mediaState = newMediaState;
                if (connection.Value is not null)
                {
                    try
                    {
                        await connection.Value.SetMediaStateAsync(mediaState: newMediaState);
                    }
                    catch (ObjectDisposedException) { }
                }
            },
            setVideoTarget: async newRemoteVideoTarget =>
            {
                videoRemoteTarget = newRemoteVideoTarget;
                if (connection.Value is not null)
                {
                    try
                    {
                        await connection.Value.SetVideoTargetAsync(videoElement: newRemoteVideoTarget);
                    }
                    catch (ObjectDisposedException) { }
                }
            },
            setLocalVideoTarget: async newLocalVideoTarget =>
            {
                localVideoTarget = newLocalVideoTarget;
                if (connection.Value is not null)
                {
                    try
                    {
                        await connection.Value.SetLocalVideoTargetAsync(videoElement: newLocalVideoTarget);
                    }
                    catch (ObjectDisposedException) { }
                }
            }
        );

        return result;
    }
}
