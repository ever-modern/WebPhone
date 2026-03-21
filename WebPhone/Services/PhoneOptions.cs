namespace WebPhone.Services;

public sealed class PhoneOptions
{
    public int PollIntervalMs { get; init; } = 1000;

    public string ExternalChannelPublishUrl { get; init; } = "/api/publish-message";

    public string ExternalChannelReadUrl { get; init; } = "/api/read-messages";

    public string PresenceAnnounceUrl { get; init; } = "/api/announce-presence";
}
