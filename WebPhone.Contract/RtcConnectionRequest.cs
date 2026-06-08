namespace WebPhone.Contract;

public record RtcConnectionRequest(
    string TargetId,
    WebRtcSessionParameter? Offer,
    WebRtcSessionParameter? Answer
) : RtcMatchParameter(Offer, Answer);
