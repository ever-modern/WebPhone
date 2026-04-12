using Microsoft.JSInterop;

namespace EverModern.Blazor.DirectCommunication;

public sealed record RtcAcceptedConnection(RtcConnection Connection, WebRtcAnswer Answer);

public sealed class RtcConnector(IJSRuntime jsRuntime)
{
    private readonly IJSRuntime _jsRuntime = jsRuntime;
    private readonly WebRtcIceServer[] _defaultIceServers = [];

    public RtcConnector(IJSRuntime jsRuntime, IEnumerable<WebRtcIceServer>? defaultIceServers)
        : this(jsRuntime)
    {
        _defaultIceServers = defaultIceServers?.ToArray() ?? [];
    }

    public async Task<RtcConnection> InitiateConnectionAsync(Func<WebRtcOffer, Task<WebRtcAnswer>> acceptOffer)
    {
        ArgumentNullException.ThrowIfNull(acceptOffer);

        var offerCallbackReference = DotNetObjectReference.Create(new OfferExchangeCallback(acceptOffer));
        var agent = new RtcConnection();

        try
        {
            var managerReference = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                "rtcConnectionManagerInterop.initiateConnectionAsync",
                offerCallbackReference,
                agent.StateChangedCallbackReference,
                _defaultIceServers);

            await agent.AttachManagerAsync(managerReference);
            return agent;
        }
        catch(Exception ex)
        {
            await agent.DisposeAsync();
            throw;
        }
        finally
        {
            offerCallbackReference.Dispose();
        }
    }

    public async Task<RtcAcceptedConnection> AcceptConnectionAsync(WebRtcOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);

        var agent = new RtcConnection();
        try
        {
            var managerReference = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                "rtcConnectionManagerInterop.acceptConnectionAsync",
                offer,
                agent.StateChangedCallbackReference,
                _defaultIceServers);

            await agent.AttachManagerAsync(managerReference);
            var answer = await agent.GetLocalAnswerAsync();
            return new RtcAcceptedConnection(agent, answer);
        }
        catch
        {
            await agent.DisposeAsync();
            throw;
        }
    }

    private sealed class OfferExchangeCallback(Func<WebRtcOffer, Task<WebRtcAnswer>> callback)
    {
        [JSInvokable]
        public Task<WebRtcAnswer> AcceptOfferAsync(WebRtcOffer offer) => callback(offer);
    }
}
