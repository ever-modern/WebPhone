using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using Microsoft.AspNetCore.Components;

namespace WebPhone;

public class UnifiedRtcConnection(
    Func<string> getId,
    Func<ValueTask> dispose,
    IValueNotifier<RtcConnectionState> state,
    INotifier<byte[]> bytesReceived,
    Func<byte[], ValueTask<bool>> writeBytes,
    Func<Task<MediaState>> getMediaState,
    Func<MediaState, Task> setMediaState,
    Func<ElementReference, Task> setVideoTarget,
    Func<ElementReference, Task> setLocalVideoTarget
) : DelegatedRtcConnection(
    getId,
    dispose,
    state,
    bytesReceived,
    writeBytes,
    getMediaState,
    setMediaState,
    setVideoTarget,
    setLocalVideoTarget
)
{
}

public static class ConnectionProxyExtensions
{
    static readonly MediaState _defaultMediaState = new(new(false, false), new(false, false));
    public static UnifiedRtcConnection GetUnifiedConnection(this PeerConnectionsDispatcher dispatcher, string peerId)
    {
        ElementReference? videoRemoteTarget = null;
        ElementReference? localVideoTarget = null;

        MediaState mediaState = _defaultMediaState;

        ObservedValue<IRtcConnection?> connection = new(null);
        ObservedValue<RtcConnectionState> state = new(connection.Value?.State.Value ?? RtcConnectionState.Disconnected);
        EventSource<byte[]> bytesReceived = new();
        Subscription? bytesSub = null;
        Func<byte[], ValueTask<bool>> writeBytes = (bytes) => connection.Value?.WriteBytesAsync(bytes) ?? ValueTask.FromResult(false);
        var connectionChangedSub = dispatcher.StateChanged.Subscribe(() =>
            {
                var newConnection = dispatcher.FindReadyConnection(peerId);
                if (newConnection?.State.Value != state.Value)
                    state.Change(newConnection?.State.Value ?? RtcConnectionState.Disconnected);
                
                if (newConnection == connection.Value)
                    return;
                
                bytesSub?.Dispose();
                bytesSub = newConnection?.BytesReceived.Subscribe(bytesReceived.Invoke);

                if (connection.Value is not null)
                {
                    if (videoRemoteTarget is not null)
                        connection.Value.SetVideoTargetAsync(videoRemoteTarget.Value);
                    if (localVideoTarget is not null)
                        connection.Value.SetLocalVideoTargetAsync(localVideoTarget.Value);

                    connection.Value.SetMediaStateAsync(mediaState);
                }

                connection.Change(newConnection);
            }
        );

        connection.Change(dispatcher.FindReadyConnection(peerId));

        Action dispose = () =>
        {
            connection.Dispose();
            connectionChangedSub.Dispose();
            state.Dispose();
        };

        UnifiedRtcConnection result = new(
            () => connection.Value?.Id ?? "undefined",
            () =>
            {
                dispose();
                return ValueTask.CompletedTask;
            },
            state,
            bytesReceived,
            writeBytes,
            async () =>
            {
                if (connection.Value is null) return new(new(false, false), new(false, false));
                return await connection.Value.GetMediaStateAsync();
            },
            async newMediaState =>
            {
                if (connection.Value is null)
                    return;

                mediaState = newMediaState;
                await connection.Value.SetMediaStateAsync(newMediaState);
            },
            async newRemoteVideoTarget =>
            {
                if (connection.Value is null)
                    return;

                videoRemoteTarget = newRemoteVideoTarget;
                await connection.Value.SetVideoTargetAsync(newRemoteVideoTarget);
            },
            async newLocalVideoTarget =>
            {
                if (connection.Value is null)
                    return;

                localVideoTarget = newLocalVideoTarget;
                await connection.Value.SetLocalVideoTargetAsync(newLocalVideoTarget);
            }
        );

        return result;
    }
}
