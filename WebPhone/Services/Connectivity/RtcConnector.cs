namespace WebPhone.Services.Connectivity;

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

public sealed record ConnectionRejectedPayload(string RequestId);
