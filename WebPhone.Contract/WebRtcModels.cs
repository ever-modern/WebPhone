using System.Text.Json.Serialization;

namespace WebPhone.Domain;

public abstract record WebRtcSessionDescription(string? Type, string? Sdp);

public record WebRtcOffer(
    string? Type,
    string? Sdp
) : WebRtcSessionDescription(Type, Sdp);

public record WebRtcAnswer(
    string? Type,
    string? Sdp
) : WebRtcSessionDescription(Type, Sdp);

public sealed record WebRtcIceCandidate
{
    [JsonPropertyName("candidate")]
    public string? Candidate { get; init; }

    [JsonPropertyName("sdpMid")]
    public string? SdpMid { get; init; }

    [JsonPropertyName("sdpMLineIndex")]
    public int? SdpMLineIndex { get; init; }

    [JsonPropertyName("usernameFragment")]
    public string? UsernameFragment { get; init; }
}

public sealed record WebRtcIceServer
{
    [JsonPropertyName("urls")]
    public string[]? Urls { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("credential")]
    public string? Credential { get; init; }
}

public sealed record WebRtcDataChannelOptions
{
    [JsonPropertyName("ordered")]
    public bool? Ordered { get; init; }

    [JsonPropertyName("maxPacketLifeTime")]
    public int? MaxPacketLifeTime { get; init; }

    [JsonPropertyName("maxRetransmits")]
    public int? MaxRetransmits { get; init; }
}
