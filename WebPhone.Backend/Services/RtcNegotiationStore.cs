using EverModern.Threading.Locks;
using WebPhone.Domain;

namespace WebPhone.Backend.Services;

public class OngoingNegotiation(
    TaskCompletionSource<RtcMatchParameters> completionSource,
    WebRtcOffer offer
)
{
    public void Complete(WebRtcAnswer answer)
        => completionSource.TrySetResult(new(offer, answer));
    public void ReplaceOffer(WebRtcOffer offer) => completionSource.TrySetResult(new(offer));
    public void Negate() => completionSource.TrySetResult(new(null, null));
    public WebRtcOffer Offer => offer;

    public Task<RtcMatchParameters> WhenCompleted => completionSource.Task;
}

public class RtcNegotiationStore() : LockingDictionary<PeersPair, OngoingNegotiation>(PairsEqualityComparer.Instance) {}
