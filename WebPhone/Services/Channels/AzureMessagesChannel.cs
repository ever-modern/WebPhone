using System.Threading.Channels;
using EverModern.Threading.Channels;
using WebPhone.Contract;
using WebPhone.Messages;

namespace WebPhone.Services.Channels;

public sealed class AzureMessagesChannel : IMessagesChannel, IAsyncDisposable
{
    private readonly List<Channel<IncomingMessage>> incomingChannels = [];
    private readonly Channel<OutgoingMessage> outgoingChannel =
        Channel.CreateUnbounded<OutgoingMessage>();
    private readonly TimeSpan idleSendInterval;
    private readonly CancellationTokenSource cts = new();
    private Task? sendLoopTask;
    private DateTime lastReadTimestamp = DateTime.UtcNow.AddSeconds(-5);
    private DateTime lastSentTimestamp = DateTime.UtcNow;
    readonly BackendClient _client;

    public AzureMessagesChannel(BackendClient client, int pollIntervalMs = 1000)
    {
        _client = client;
        idleSendInterval = TimeSpan.FromMilliseconds(Math.Max(pollIntervalMs, 250));
        lastSentTimestamp = DateTime.UtcNow - idleSendInterval;
    }

    public ChannelWriter<OutgoingMessage> Writer => outgoingChannel.Writer;

    public IChannelSubscription<IncomingMessage> Subscribe() => Subscribe(null);

    public IChannelSubscription<IncomingMessage> Subscribe(Func<IncomingMessage, bool>? filter)
    {
        var channel = Channel.CreateUnbounded<IncomingMessage>();

        var result = new ChannelSubscription<IncomingMessage>(
            channel.Reader,
            (self) => incomingChannels.Remove(channel),
            filter
        );

        incomingChannels.Add(channel);

        return result;
    }

    public Task StartAsync() => sendLoopTask = RunSendLoopAsync(cts.Token);

    private async Task RunSendLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var waitForMessageTask = outgoingChannel
                .Reader.WaitToReadAsync(cancellationToken)
                .AsTask();
            var idleDelayTask = Task.Delay(GetIdleDelay(), cancellationToken);
            var completedTask = await Task.WhenAny(waitForMessageTask, idleDelayTask);

            if (completedTask == waitForMessageTask)
            {
                if (!await waitForMessageTask)
                {
                    break;
                }

                await SendExchangeAsync(DrainOutgoingMessages(), cancellationToken);
                continue;
            }

            await SendExchangeAsync([], cancellationToken);
        }
    }

    private TimeSpan GetIdleDelay()
    {
        var elapsedSinceLastSend = DateTime.UtcNow - lastSentTimestamp;
        if (elapsedSinceLastSend >= idleSendInterval)
        {
            return TimeSpan.Zero;
        }

        return idleSendInterval - elapsedSinceLastSend;
    }

    private async Task SendExchangeAsync(
        MessageRequest[] outgoingMessages,
        CancellationToken cancellationToken
    )
    {
        var requestStartTimestamp = DateTime.UtcNow;
        var exchangeResponse = await _client.ExchangeAsync(
            outgoingMessages,
            lastReadTimestamp,
            cancellationToken
        );
        var messages = exchangeResponse?.RelevantMessages;
        if (messages is null)
        {
            lastReadTimestamp = requestStartTimestamp;
            lastSentTimestamp = requestStartTimestamp;
            return;
        }

        foreach (var message in messages)
        {
            foreach (var incomingChannel in incomingChannels)
            {
                IncomingMessage incomingMessage = new(
                    MessageTypeJsonConverter.FromWireValue(message.Type),
                    message.Payload,
                    message.PublisherClientId,
                    message.DateTime
                );

                await incomingChannel.Writer.WriteAsync(incomingMessage, cancellationToken);
            }
        }

        lastReadTimestamp = requestStartTimestamp;
        lastSentTimestamp = requestStartTimestamp;
    }

    private MessageRequest[] DrainOutgoingMessages()
    {
        var messages = new List<MessageRequest>();
        while (outgoingChannel.Reader.TryRead(out var message))
        {
            var targetClientId = message.TargetClientId;
            messages.Add(
                new MessageRequest(
                    MessageTypeJsonConverter.ToWireValue(message.Type),
                    message.Payload,
                    targetClientId
                )
            );
        }

        return [.. messages];
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        cts.Cancel();
        outgoingChannel.Writer.TryComplete();
        incomingChannels.ForEach(ch => ch.Writer.TryComplete());

        if (sendLoopTask is not null)
            await sendLoopTask.ContinueWith(_ => Task.CompletedTask);

        cts.Dispose();
    }
}
