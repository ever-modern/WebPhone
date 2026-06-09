namespace WebPhone.Contract;

public record RtcConnectionRequest(
    string TargetId,
    WebRtcOffer? Offer,
    WebRtcAnswer? Answer
) : RtcMatchParameter(Offer, Answer);
