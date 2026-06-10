using System.Collections.Concurrent;
using WebPhone.Domain;

namespace WebPhone.Backend.Services;

public class WebRtcParametersStorage
    : ConcurrentDictionary<PeersPair, (WebRtcOffer, TaskCompletionSource<RtcMatchParameter>)>
{
    public WebRtcParametersStorage()
        : base(PairsEqualityComparer.Instance) { }
}
