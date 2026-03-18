using System.Text.Json.Serialization;

namespace EverModern.Blazor.DirectCommunication;

public sealed record WebRtcSessionDescription
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("sdp")]
    public string? Sdp { get; init; }
}

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

public sealed record WebRtcConnectionStateChangedEventArgs(string ConnectionId, string State);

public sealed record WebRtcDataChannelStateChangedEventArgs(string ConnectionId, string State);

public sealed record WebRtcDataMessageEventArgs(string ConnectionId, string Message);

public sealed record WebRtcRemoteStreamEventArgs(string ConnectionId);

public sealed record WebRtcIceCandidateEventArgs(string ConnectionId, WebRtcIceCandidate Candidate);
