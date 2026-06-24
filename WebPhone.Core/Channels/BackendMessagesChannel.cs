using System.Collections.Concurrent;
using System.Threading.Channels;
using EverModern.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using WebPhone.Domain;
using WebPhone.Domain.Communication;
using WebPhone.Messages;

namespace WebPhone.Channels;

public class BackendMessagesChannel : IMessagesChannel, IAsyncDisposable
{
    readonly ConcurrentDictionary<Channel<IncomingMessage>, byte> _incomingChannels = new();
    readonly Lock _starting = new();

    readonly Channel<OutgoingMessage> _outgoingChannel =
        Channel.CreateBounded<OutgoingMessage>(50);

    readonly CancellationTokenSource _cts = new();

    public BackendMessagesChannel() {}

    public static async Task<BackendMessagesChannel> BindAsync(HubConnection hubConnection)
    {
        var channel = new BackendMessagesChannel();
        await channel.StartAsync(hubConnection);
        return channel;
    }

    public async Task StartAsync(HubConnection hubConnection)
    {
        if (_starting.TryEnter() is false)
            return;

        var ct = _cts.Token;

        hubConnection.On<ReceivedMessage>(
            nameof(MessageSpecifications.Push),
            exchange =>
            {
                IncomingMessage message = new(
                    -1,
                    exchange.Type,
                    exchange.Payload,
                    exchange.Sender,
                    DateTime.UtcNow
                );

                foreach (var subscriber in _incomingChannels.Keys)
                {
                    var writer = subscriber.Writer;

                    writer.TryWrite(message);
                }

                return Task.CompletedTask;
            }
        );

        TaskCompletionSource startedReading = new();

        _ = Task.Run(
            async () =>
            {
                await foreach (
                    var message in _outgoingChannel.Reader.ReadAllAsync(ct).Prepend(null!)
                )
                {
                    if (message is null)
                    {
                        startedReading.TrySetResult();
                        continue;
                    }

                    await hubConnection.InvokeAsync(
                        nameof(MessageSpecifications.Send),
                        message,
                        ct
                    );
                }
            },
            ct
        );

        await startedReading.Task;
    }

    public ChannelWriter<OutgoingMessage> Writer => _outgoingChannel.Writer;

    public IChannelSubscription<IncomingMessage> Subscribe(
        Func<IncomingMessage, bool> filter)
    {
        var channel = Channel.CreateBounded<IncomingMessage>(50);

        _incomingChannels.TryAdd(channel, 0);

        return new ChannelSubscription<IncomingMessage>(
            channel.Reader,
            _ =>
            {
                if (_incomingChannels.TryRemove(channel, out var _))
                {
                    channel.Writer.TryComplete();
                }
            },
            filter
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_starting.TryEnter())
            return;

        _outgoingChannel.Writer.TryComplete();

        await _cts.CancelAsync();

        foreach (var channel in _incomingChannels.Keys)
        {
            channel.Writer.TryComplete();
        }

        _cts.Dispose();
    }
}
