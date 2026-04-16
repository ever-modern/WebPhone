using Microsoft.JSInterop;

namespace EverModern.Blazor.DirectCommunication;

public sealed class RtcConnector(IJSRuntime jsRuntime, IEnumerable<WebRtcIceServer> iceServers)
{
    public async Task<RtcConnection> InitiateConnectionAsync(
        Func<WebRtcOffer, Task<WebRtcAnswer>> getAnswer
    )
    {
        ArgumentNullException.ThrowIfNull(getAnswer);

        var stateChanged = new Events.EventSource<string>();
        var channelMessageReceived = new Events.EventSource<byte[]>();

        var offerCallbackReference = DotNetObjectReference.Create(getAnswer);

        var managerReference = await jsRuntime.InvokeAsync<IJSObjectReference>(
            "rtcConnectionFactory.initiateConnectionAsync",
            [
                offerCallbackReference,
                iceServers,
                DotNetObjectReference.Create(getAnswer),
                DotNetObjectReference.Create(stateChanged.Invoke),
                DotNetObjectReference.Create(channelMessageReceived.Invoke),
            ]
        );

        var onDispose = () =>
        {
            _ = managerReference.InvokeVoidAsync("close", []);
        };

        var result = new RtcConnection(
            onDispose,
            stateChanged,
            channelMessageReceived,
            async () => await managerReference.InvokeAsync<string>("getState"),
            bytes => _ = managerReference.InvokeVoidAsync("writeToChannel", bytes)
        );

        return result;
    }

    public async Task<RtcConnection> AcceptConnectionAsync(
        WebRtcOffer offer,
        Func<WebRtcAnswer, Task> sendAnswerBack
    )
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(sendAnswerBack);

        var stateChanged = new Events.EventSource<string>();
        var channelMessageReceived = new Events.EventSource<byte[]>();

        var managerReference = await jsRuntime.InvokeAsync<IJSObjectReference>(
            "rtcConnectionFactory.acceptConnectionAsync",
            [
                iceServers,
                offer,
                DotNetObjectReference.Create(sendAnswerBack),
                DotNetObjectReference.Create(stateChanged.Invoke),
                DotNetObjectReference.Create(channelMessageReceived.Invoke),
            ]
        );

        var onDispose = () =>
        {
            _ = managerReference.InvokeVoidAsync("close", []);
        };

        var result = new RtcConnection(
            onDispose,
            stateChanged,
            channelMessageReceived,
            async () => await managerReference.InvokeAsync<string>("getState"),
            bytes => _ = managerReference.InvokeVoidAsync("writeToChannel", bytes)
        );

        return result;
    }
}
