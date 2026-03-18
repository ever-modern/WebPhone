namespace WebPhone.Registration;

public interface IWebRtcConfigurator : IAsyncDisposable
{
    ValueTask ConfigureAsync(ChannelsConfiguration configuration, CancellationToken cancellationToken = default);

    ValueTask InitializeAsync(string channelName, string eventName, CancellationToken cancellationToken = default);
}

public interface IWebRtcConnector
{
    ValueTask PublishAsync(string channelName, string eventName, object payload, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<Message>> PollMessagesAsync(string channelName, CancellationToken cancellationToken = default);
}
