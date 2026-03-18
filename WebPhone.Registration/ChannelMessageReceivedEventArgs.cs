namespace WebPhone.Registration;

public sealed class ChannelMessageReceivedEventArgs(string channelName, string eventName, string payloadJson) : EventArgs
{
    public string ChannelName { get; } = channelName;

    public string EventName { get; } = eventName;

    public string PayloadJson { get; } = payloadJson;
}