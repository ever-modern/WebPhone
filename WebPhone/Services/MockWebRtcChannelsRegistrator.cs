using WebPhone.Registration;

namespace WebPhone.Services;

public sealed class MockWebRtcChannelsRegistrator : IWebRtcConfigurator, IWebRtcConnector
{
    public ValueTask ConfigureAsync(ChannelsConfiguration configuration, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask InitializeAsync(string channelName, string eventName, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask PublishAsync(string channelName, string eventName, object payload, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<Message>> PollMessagesAsync(string channelName, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>());

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;
}
