using Microsoft.JSInterop;

namespace EverModern.Blazor.DirectCommunication;

public sealed record RtcAcceptedConnection(RtcConnectionAgent Connection, WebRtcAnswer Answer);

public sealed class WebRtcConnector(IJSRuntime jsRuntime)
{
    private readonly IJSRuntime _jsRuntime = jsRuntime;

    public async Task<RtcConnectionAgent> InitiateConnectionAsync(Func<WebRtcOffer, Task<WebRtcAnswer>> acceptOffer)
    {
        ArgumentNullException.ThrowIfNull(acceptOffer);

        var offerCallbackReference = DotNetObjectReference.Create(new OfferExchangeCallback(acceptOffer));
        var agent = new RtcConnectionAgent();

        try
        {
            var managerReference = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                "rtcConnectionManagerInterop.initiateConnectionAsync",
                offerCallbackReference,
                agent.StateChangedCallbackReference);

            await agent.AttachManagerAsync(managerReference);
            return agent;
        }
        catch
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

        var agent = new RtcConnectionAgent();
        try
        {
            var managerReference = await _jsRuntime.InvokeAsync<IJSObjectReference>(
                "rtcConnectionManagerInterop.acceptConnectionAsync",
                offer,
                agent.StateChangedCallbackReference);

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
