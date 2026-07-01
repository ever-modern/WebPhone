using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace WebPhone.Domain;

public record RtcMatchParameters(WebRtcOffer? Offer, WebRtcAnswer? Answer)
{
    public RtcMatchParameters(WebRtcOffer offer)
        : this(offer, null) { }

    public static long ComputeNegotiationId(WebRtcOffer offer, WebRtcAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(offer, nameof(offer));
        ArgumentNullException.ThrowIfNull(answer, nameof(answer));
        var bytes = Encoding.UTF8.GetBytes($"{offer}|{answer}");

        var hash = SHA256.HashData(bytes);

        return BitConverter.ToInt64(hash, 0);
    }
}

public record RtcMatchResponse(WebRtcOffer? Offer, WebRtcAnswer? Answer)
    : RtcMatchParameters(Offer, Answer)
{
    public string? Id { get; } =
        Offer is not null && Answer is not null
            ? ComputeNegotiationId(Offer, Answer).ToString()
            : null;
}
