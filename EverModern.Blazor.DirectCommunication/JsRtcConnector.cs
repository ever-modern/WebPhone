using EverModern.Events;
using Microsoft.JSInterop;
using WebPhone.Domain;

namespace EverModern.Blazor.DirectCommunication;

public sealed class JsRtcConnector(
    IJSRuntime jsRuntime,
    IEnumerable<WebRtcIceServer> iceServers
)
    : IRtcConnector
{
    public async Task<IRtcConnection?> InitiateConnectionAsync(
        Func<WebRtcOffer, Task<(WebRtcAnswer? Answer, RtcConnectionId? ConnectionId)>> getAnswer,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(argument: getAnswer);

        var channelMessageReceived = new Events.EventSource<byte[]>();

        var getAnswerLink = DotNetObjectReference.Create(
            value: JsFunction.Create<WebRtcOffer, Task<object>>(
                func: async offer =>
                {
                    var (answer, connectionId) = await getAnswer(offer);
                    return new
                    {
                        answer,
                        connectionId
                    };
                }
            )
        );

        var state = new ObservedValue<RtcConnectionState>(RtcConnectionState.New);

        var managerReference = await jsRuntime.InvokeAsync<IJSObjectReference>(
            identifier: "rtcConnectionFactory.initiateConnectionAsync",
            cancellationToken: cancellationToken,
            args:
            [
                iceServers,
                getAnswerLink,
                DotNetObjectReference.Create(value: JsFunction.Create(func: (string newState) => state.Change(newValue: IRtcConnection.StateFromString(newState)))),
                DotNetObjectReference.Create(value: JsFunction.Create(func: (byte[] bytes) => channelMessageReceived.Invoke(newValue: bytes))),
            ]
        );

        if (managerReference is null)
            return null;

        var onDispose = () => managerReference.InvokeVoidAsync(identifier: "close", args: []);

        var id = await managerReference.InvokeAsync<string>(identifier: "getId");

        var result = new BrowserRtcConnection(
            () => id,
            dispose: onDispose,
            state: state,
            bytesReceived: channelMessageReceived,
            writeBytes: async bytes => await managerReference.InvokeAsync<bool>(identifier: "writeToChannel", args: bytes),
            getMediaState: async () => await managerReference.InvokeAsync<MediaState>(identifier: "getMediaState", args: []),
            setMediaState: async (mediaState) =>
                await managerReference.InvokeVoidAsync(identifier: "setMediaState", args: mediaState),
            setVideoTarget: async (videoElement) =>
                await managerReference.InvokeVoidAsync(identifier: "setVideoTarget", args: videoElement),
            setLocalVideoTarget: async (videoElement) =>
                await managerReference.InvokeVoidAsync(identifier: "setLocalVideoTarget", args: videoElement)
        );

        await WhenOpen(connection: result);

        return result;
    }
    public ValueTask CloseConnectionAsync(IRtcConnection connection)
    {
        var browserConnection = connection as BrowserRtcConnection
                                ?? throw new InvalidOperationException($"Passed connection is not a {nameof(BrowserRtcConnection)}.");
        return browserConnection.DisposeAsync();
    }

    public async Task<IRtcConnection?> AcceptConnectionAsync(
        WebRtcOffer offer,
        Func<WebRtcAnswer, Task<RtcConnectionId>> sendAnswerBack,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(sendAnswerBack);

        var state = new ObservedValue<RtcConnectionState>(RtcConnectionState.New);
        var channelMessageReceived = new Events.EventSource<byte[]>();

        var managerReference = await jsRuntime.InvokeAsync<IJSObjectReference>(
            "rtcConnectionFactory.acceptConnectionAsync",
            cancellationToken,
            [
                iceServers,
                offer,
                DotNetObjectReference.Create(JsFunction.Create(sendAnswerBack)),
                DotNetObjectReference.Create(value: JsFunction.Create(func: (string newState) => state.Change(newValue: IRtcConnection.StateFromString(newState)))),
                DotNetObjectReference.Create(JsFunction.Create((byte[] bytes) => channelMessageReceived.Invoke(bytes))),
            ]
        ) ?? throw new RtcConnectionException("Failed to create RTC connection.");

        var dispose = () => managerReference.InvokeVoidAsync("close", []);

        var id = await managerReference.InvokeAsync<string>(identifier: "getId");

        var result = new BrowserRtcConnection(
            () => id,
            dispose,
            state,
            channelMessageReceived,
            async bytes => await managerReference.InvokeAsync<bool>("writeToChannel", bytes),
            async () => await managerReference.InvokeAsync<MediaState>("getMediaState", []),
            async (mediaState) =>
                await managerReference.InvokeVoidAsync("setMediaState", mediaState),
            async (videoElement) =>
                await managerReference.InvokeVoidAsync("setVideoTarget", videoElement),
            async (videoElement) =>
                await managerReference.InvokeVoidAsync("setLocalVideoTarget", videoElement)
        );

        await WhenOpen(result);

        return result;
    }

    async Task WhenOpen(IRtcConnection connection)
    {
        var tcs = new TaskCompletionSource();

        using var _ = connection.State.Subscribe(newState =>
            {
                if (newState is RtcConnectionState.Connected)
                {
                    tcs.TrySetResult();
                    return;
                }

                if (newState is RtcConnectionState.Closed)
                {
                    tcs.TrySetException(new RtcConnectionException("Could not connect."));
                }
            }
        );

        var currentState = connection.State.Value;

        if (currentState is RtcConnectionState.Connected)
        {
            tcs.TrySetResult();
        }

        await tcs.Task;
    }
}
