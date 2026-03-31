using Microsoft.AspNetCore.Authorization;
using System.Threading.Channels;
using WebPhone.Contract;
using WebPhone.Registration;

namespace WebPhone.Services;

public sealed class AzureMessagesChannel : IMessagesChannel, IAsyncDisposable
{
    private readonly List<Channel<IncomingMessage>> incomingChannels = [];
    private readonly Channel<OutgoingMessage> outgoingChannel = Channel.CreateUnbounded<OutgoingMessage>();
    private readonly TimeSpan idleSendInterval;
    private readonly CancellationTokenSource cts = new();
    private readonly Task sendLoopTask;
    private DateTimeOffset lastReadTimestamp = DateTimeOffset.UtcNow.AddSeconds(-5);
    private DateTimeOffset lastSentTimestamp = DateTimeOffset.UtcNow;
    readonly BackendClient _client;
    
    public AzureMessagesChannel(BackendClient client, int pollIntervalMs = 1000)
    {
        _client = client;
        idleSendInterval = TimeSpan.FromMilliseconds(Math.Max(pollIntervalMs, 250));
        sendLoopTask = RunSendLoopAsync(cts.Token);
    }

    public ChannelWriter<OutgoingMessage> Writer => outgoingChannel.Writer;

    public IChannelSubscription<IncomingMessage> Subscribe()
        => Subscribe(null);

    public IChannelSubscription<IncomingMessage> Subscribe(Func<IncomingMessage, bool>? filter)
    {
        var channel = Channel.CreateUnbounded<IncomingMessage>();

        var result = new ChannelSubscription<IncomingMessage>(
            channel.Reader,
            (self) => incomingChannels.Remove(channel),
            filter);

        incomingChannels.Add(channel);

        return result;
    }

    private async Task RunSendLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var waitForMessageTask = outgoingChannel.Reader.WaitToReadAsync(cancellationToken).AsTask();
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

            // No longer send an empty message; presence messages are now sent by the phone service.
            // Avoid tight loop: if GetIdleDelay is already zero (we would have sent an empty heartbeat),
            // pause for the configured idle interval to prevent busy-spinning.
            if (GetIdleDelay() <= TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(idleSendInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // cancellation requested - break the loop on next iteration
                }
            }
        }
    }

    private TimeSpan GetIdleDelay()
    {
        var elapsedSinceLastSend = DateTimeOffset.UtcNow - lastSentTimestamp;
        if (elapsedSinceLastSend >= idleSendInterval)
        {
            return TimeSpan.Zero;
        }

        return idleSendInterval - elapsedSinceLastSend;
    }

    private async Task SendExchangeAsync(MessageRequest[] outgoingMessages, CancellationToken cancellationToken)
    {
        var requestStartTimestamp = DateTimeOffset.UtcNow;
        var exchangeResponse = await _client.ExchangeAsync(outgoingMessages, lastReadTimestamp, cancellationToken);
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
                    _client.ClientId,
                    message.DateTime);

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
            messages.Add(new MessageRequest(MessageTypeJsonConverter.ToWireValue(message.Type), message.Payload, targetClientId));
        }

        return [.. messages];
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        cts.Cancel();
        outgoingChannel.Writer.TryComplete();
        incomingChannels.ForEach(ch => ch.Writer.TryComplete());

        await sendLoopTask.ContinueWith(_ => Task.CompletedTask);

        cts.Dispose();
    }
}
