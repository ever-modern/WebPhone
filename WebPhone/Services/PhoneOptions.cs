namespace WebPhone.Services;

public sealed class PhoneOptions
{
    public int PollIntervalMs { get; init; } = 1000;

    public string ExternalChannelBaseUrl { get; init; } = "/.netlify/functions/pusher-events";

    public string ExternalChannelPollPath { get; init; } = "poll";
}
