using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using WebPhone.Contract;

namespace WebPhone.Tests.Provision;

class MockWebRtcConnector : IRtcConnector
{
    public int InitiateCalls { get; private set; }
    public int AcceptCalls { get; private set; }

    public Func<Func<WebRtcOffer, Task<WebRtcAnswer?>>, CancellationToken, Task<BrowserRtcConnection?>>?
        OnInitiateAsync { get; set; }

    public Func<WebRtcOffer, Func<WebRtcAnswer, Task>, CancellationToken, Task<BrowserRtcConnection?>>?
        OnAcceptAsync { get; set; }

    public async Task<IRtcConnection?> AcceptConnectionAsync(
        WebRtcOffer offer,
        Func<WebRtcAnswer, Task> sendAnswerBack,
        CancellationToken cancellationToken
    )
    {
        AcceptCalls++;

        if (OnAcceptAsync is not null)
        {
            return await OnAcceptAsync(offer, sendAnswerBack, cancellationToken);
        }

        return CreateConnectedRtcConnection();
    }

    public async Task<IRtcConnection?> InitiateConnectionAsync(
        Func<WebRtcOffer, Task<WebRtcAnswer?>> getAnswer,
        CancellationToken cancellationToken
    )
    {
        InitiateCalls++;

        if (OnInitiateAsync is not null)
        {
            return await OnInitiateAsync(getAnswer, cancellationToken);
        }

        return CreateConnectedRtcConnection();
    }

    public static BrowserRtcConnection CreateConnectedRtcConnection() =>
        new(
            dispose: () => { },
            stateChanged: new EventSource<string>(),
            bytesReceived: new EventSource<byte[]>(),
            getState: () => Task.FromResult("connected"),
            writeBytes: _ => ValueTask.FromResult(true),
            getMediaState: () =>
                Task.FromResult(
                    new MediaState(
                        new MediaPartState(true, true),
                        new MediaPartState(true, true)
                    )
                ),
            setMediaState: _ => Task.CompletedTask,
            setVideoTarget: _ => Task.CompletedTask,
            setLocalVideoTarget: _ => Task.CompletedTask
        );
}
