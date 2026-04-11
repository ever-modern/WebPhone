namespace WebPhone.Services;

public enum RtcConnectionState
{
    New,
    Connecting,
    Connected,
    Disconnected,
    Recovering,
    Failed,
    Closed,
}

public interface IRtcConnection : IDisposable
{
    string Id { get; }

    string RemotePeer { get; }

    RtcConnectionState State { get; }

    void SetState(RtcConnectionState state);

    event Action<RtcConnectionState> StateChanged;
}

[Obsolete(
    "RtcConnector is suspended. Use PeerConnector and WebRtcConnector/RtcConnectionAgent instead.",
    true
)]
public sealed class RtcConnector;

public sealed record ConnectionRejectedPayload(string RequestId);
