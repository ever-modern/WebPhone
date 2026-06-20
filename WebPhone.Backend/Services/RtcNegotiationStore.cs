using EverModern.Threading.Locks;
using WebPhone.Domain;

namespace WebPhone.Backend.Services;

public class OngoingNegotiation(
    TaskCompletionSource<RtcMatchParameter> completionSource,
    WebRtcOffer offer
)
{
    public void Complete(WebRtcAnswer answer)
        => completionSource.TrySetResult(new(offer, answer));
    public void ReplaceOffer(WebRtcOffer offer) => completionSource.TrySetResult(new(offer, null));
    public void Negate() => completionSource.TrySetResult(new(null, null));
    public WebRtcOffer Offer => offer;

    public Task<RtcMatchParameter> WhenCompleted => completionSource.Task;
}

public class RtcNegotiationStore() : LockingDictionary<PeersPair, OngoingNegotiation>(PairsEqualityComparer.Instance) {}

