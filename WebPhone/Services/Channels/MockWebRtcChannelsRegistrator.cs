using WebPhone.Messages;

namespace WebPhone.Services.Channels;

public sealed class MockWebRtcChannelsRegistrator : IWebRtcConfigurator, IWebRtcRegistrator
{
    public ValueTask ConfigureAsync(ChannelsConfiguration configuration, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask InitializeAsync(string channelName, string eventName, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask PublishAsync(string channelName, string eventName, object payload, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<OutgoingMessage>> PollMessagesAsync(string channelName, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<OutgoingMessage>>(Array.Empty<OutgoingMessage>());

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;
}
