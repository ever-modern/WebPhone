namespace WebPhone.Registration.Pusher;

public sealed class PusherOptions
{
    public string AppId { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public string Secret { get; init; } = string.Empty;

    public string Cluster { get; init; } = string.Empty;

    public string ChannelPrefix { get; init; } = "webrtc";

    public string EventName { get; init; } = "signal";

    public bool EnableLogging { get; init; }

    public string? ProxyUrl { get; init; }

    public string? AuthUrl { get; init; }

    public bool UseClientEvents { get; init; } = true;

    public int PollIntervalMs { get; init; } = 1000;
}
