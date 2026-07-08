using EverModern.Threading.Locks;
using WebPhone.Domain;

namespace WebPhone.Backend.Services;

public class OngoingNegotiation(
    TaskCompletionSource<RtcMatchParameters> completionSource,
    WebRtcOffer initialOffer
)
{
    WebRtcOffer _offer = initialOffer;

    public void Complete(WebRtcAnswer answer)
        => completionSource.TrySetResult(new(_offer, answer));
    public void ReplaceOffer(WebRtcOffer newOffer) => _offer = newOffer;
    public void Negate() => completionSource.TrySetResult(new(null, null));
    public WebRtcOffer Offer => _offer;

    public Task<RtcMatchParameters> WhenCompleted => completionSource.Task;
}

public class RtcNegotiationStore() : LockingDictionary<PeersPair, OngoingNegotiation>(PairsEqualityComparer.Instance) {}
