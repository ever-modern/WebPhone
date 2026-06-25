using Microsoft.JSInterop;
using WebPhone.Domain;

namespace EverModern.Blazor.DirectCommunication;

public class JsInvokableFunc<Tout>(
    Func<Tout> func
)
{
    [JSInvokable("invoke")]
    public Tout Invoke() => func();
}

public class JsInvokableFunc<TIn, TOut>(
    Func<TIn, TOut> func
)
{
    [JSInvokable("invoke")]
    public TOut Invoke(TIn p) => func(p);
}

public class JsInvokableAction<TIn>(
    Action<TIn> func
)
{
    [JSInvokable("invoke")]
    public void Invoke(TIn p1) => func(p1);
}

public static class JsFunction
{
    public static JsInvokableFunc<TOut> Create<TOut>(Func<TOut> func) => new(func);

    public static JsInvokableFunc<TIn, TOut> Create<TIn, TOut>(Func<TIn, TOut> func) => new(func);

    public static JsInvokableAction<TIn> Create<TIn>(Action<TIn> func) => new(func);
}

public record struct ConnectionInitiationResult(
    BrowserRtcConnection? Connection,
    WebRtcOffer? CounterOffer
);

public sealed class JsRtcConnector(
    IJSRuntime jsRuntime,
    IEnumerable<WebRtcIceServer> iceServers
)
    : IRtcConnector
{
    public async Task<IRtcConnection?> InitiateConnectionAsync(
        Func<WebRtcOffer, Task<WebRtcAnswer?>> getAnswer,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(getAnswer);

        var stateChanged = new Events.EventSource<string>();
        var channelMessageReceived = new Events.EventSource<byte[]>();

        var getAnswerLink = DotNetObjectReference.Create(JsFunction.Create(getAnswer));

        var managerReference = await jsRuntime.InvokeAsync<IJSObjectReference>(
            "rtcConnectionFactory.initiateConnectionAsync",
            cancellationToken,
            [
                iceServers,
                getAnswerLink,
                DotNetObjectReference.Create(JsFunction.Create((string state) => stateChanged.Invoke(state))),
                DotNetObjectReference.Create(JsFunction.Create((byte[] bytes) => channelMessageReceived.Invoke(bytes))),
            ]
        );

        if (managerReference is null)
            return null;

        var onDispose = () =>
        {
            _ = managerReference.InvokeVoidAsync("close", []);
        };

        var result = new BrowserRtcConnection(
            onDispose,
            stateChanged,
            channelMessageReceived,
            async () => await managerReference.InvokeAsync<string>("getState"),
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

    public async Task<IRtcConnection?> AcceptConnectionAsync(
        WebRtcOffer offer,
        Func<WebRtcAnswer, Task<bool>> sendAnswerBack,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(sendAnswerBack);

        var stateChanged = new Events.EventSource<string>();
        var channelMessageReceived = new Events.EventSource<byte[]>();

        var managerReference = await jsRuntime.InvokeAsync<IJSObjectReference>(
            "rtcConnectionFactory.acceptConnectionAsync",
            cancellationToken,
            [
                iceServers,
                offer,
                DotNetObjectReference.Create(JsFunction.Create(sendAnswerBack)),
                DotNetObjectReference.Create(JsFunction.Create((string state) => stateChanged.Invoke(state))),
                DotNetObjectReference.Create(JsFunction.Create((byte[] bytes) => channelMessageReceived.Invoke(bytes))),
            ]
        ) ?? throw new RtcConnectionException("Failed to create RTC connection.");

        var onDispose = () =>
        {
            _ = managerReference.InvokeVoidAsync("close", []).AsTask();
        };

        var result = new BrowserRtcConnection(
            onDispose,
            stateChanged,
            channelMessageReceived,
            async () => await managerReference.InvokeAsync<string>("getState"),
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

        using var _ = connection.StateChanged.Subscribe(newState =>
            {
                if (newState is "connected")
                {
                    tcs.TrySetResult();
                    return;
                }

                if (newState is "closed")
                {
                    tcs.TrySetException(new RtcConnectionException("Could not connect."));
                }
            }
        );

        var currentState = await connection.GetStateAsync();

        if (currentState is "connected")
        {
            tcs.TrySetResult();
        }

        await tcs.Task;
    }
}
