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
        BytesChannel bytesChannel = new(
            (bytes) =>
                connection.Value?.Bytes.WriteAsync(bytes) ?? ValueTask.FromResult(result: false),
            bytesReceived
        );
        var connectionChangedSub = dispatcher.ConnectionsChange.SubscribeAfter(() =>
        {
            var newConnection = dispatcher.FindReadyConnection(peerId: peerId);
            if (newConnection?.State.Value != state.Value)
                state.Change(
                    newValue: newConnection?.State.Value ?? RtcConnectionState.Disconnected
                );

            if (newConnection == connection.Value)
                return;

            bytesSub?.Dispose();
            bytesSub = newConnection?.Bytes?.Received.Subscribe(handler: bytesReceived.Invoke);

            if (connection.Value is not null)
            {
                if (videoRemoteTarget is not null)
                    connection.Value.SetVideoTargetAsync(videoElement: videoRemoteTarget.Value);
                if (localVideoTarget is not null)
                    connection.Value.SetLocalVideoTargetAsync(videoElement: localVideoTarget.Value);

                connection.Value.SetMediaStateAsync(mediaState: mediaState);
            }

            connection.Change(newValue: newConnection);
        });

        connection.Change(newValue: dispatcher.FindReadyConnection(peerId: peerId));

        var initialConnection = connection.Value;
        if (initialConnection is not null)
        {
            state.Change(newValue: initialConnection.State.Value);
            bytesSub = initialConnection.Bytes.Received.Subscribe(handler: bytesReceived.Invoke);
        }

        Action dispose = () =>
        {
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
                return await connection.Value.GetMediaStateAsync();
            },
            setMediaState: async newMediaState =>
            {
                if (connection.Value is null)
                    return;

                mediaState = newMediaState;
                await connection.Value.SetMediaStateAsync(mediaState: newMediaState);
            },
            setVideoTarget: async newRemoteVideoTarget =>
            {
                if (connection.Value is null)
                    return;

                videoRemoteTarget = newRemoteVideoTarget;
                await connection.Value.SetVideoTargetAsync(videoElement: newRemoteVideoTarget);
            },
            setLocalVideoTarget: async newLocalVideoTarget =>
            {
                if (connection.Value is null)
                    return;

                localVideoTarget = newLocalVideoTarget;
                await connection.Value.SetLocalVideoTargetAsync(videoElement: newLocalVideoTarget);
            }
        );

        return result;
    }
}
