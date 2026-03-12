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

    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }

    [JsonPropertyName("negotiated")]
    public bool? Negotiated { get; init; }

    [JsonPropertyName("id")]
    public ushort? Id { get; init; }
}

public sealed class WebRtcIceCandidateEventArgs(string connectionId, WebRtcIceCandidate candidate) : EventArgs
{
    public string ConnectionId { get; } = connectionId;

    public WebRtcIceCandidate Candidate { get; } = candidate;
}

public sealed class WebRtcConnectionStateChangedEventArgs(string connectionId, string state) : EventArgs
{
    public string ConnectionId { get; } = connectionId;

    public string State { get; } = state;
}

public sealed class WebRtcDataChannelStateChangedEventArgs(string connectionId, string state) : EventArgs
{
    public string ConnectionId { get; } = connectionId;

    public string State { get; } = state;
}

public sealed class WebRtcDataMessageEventArgs(string connectionId, string message) : EventArgs
{
    public string ConnectionId { get; } = connectionId;

    public string Message { get; } = message;
}

public sealed class WebRtcRemoteStreamEventArgs(string connectionId) : EventArgs
{
    public string ConnectionId { get; } = connectionId;
}
