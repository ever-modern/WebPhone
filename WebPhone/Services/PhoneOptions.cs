using EverModern.Blazor.DirectCommunication;

namespace WebPhone.Services;

public sealed class PhoneOptions
{
    public int PollIntervalMs { get; init; } = 1000;

    public string ExternalChannelBaseUrl { get; init; } = "/";

    public WebRtcIceServer[] WebRtcIceServers { get; init; } = [];
}   
