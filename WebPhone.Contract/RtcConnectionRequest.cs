namespace WebPhone.Domain;

public record RtcConnectionRequest(
    string TargetId,
    WebRtcOffer? Offer,
    WebRtcAnswer? Answer
) : RtcMatchParameters(Offer, Answer);
