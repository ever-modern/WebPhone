using System.Threading.Channels;
using EverModern.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using WebPhone.Domain;
using WebPhone.Domain.Communication;
using WebPhone.Messages;

namespace WebPhone.Services.Channels;

public sealed class BackendMessagesChannel : IMessagesChannel, IAsyncDisposable
{
    readonly List<Channel<IncomingMessage>> _incomingChannels = new();
    readonly Channel<OutgoingMessage> _outgoingChannel = Channel.CreateBounded<OutgoingMessage>(50);
    readonly CancellationTokenSource _cts = new();
    readonly TaskCompletionSource _whenReadyTcs = new();
    public Task WhenReady => _whenReadyTcs.Task;

    public BackendMessagesChannel(HubConnection hubConnection) : this(Task.FromResult(hubConnection)) {}

    public BackendMessagesChannel(Task<HubConnection> openingHubConnection)
    {
        _ = Task.Run(async () =>
            {
                var hubConnection = await openingHubConnection;

                hubConnection.On<ExchangeResponse>(
                    nameof(MessageSpecifications.Push),
                    async exchange =>
                    {
                        var messages = exchange?.RelevantMessages.Select(rm => new IncomingMessage(
                                rm.Id,
                                MessageTypeJsonConverter.FromWireValue(rm.Type),
                                rm.Payload,
                                rm.PublisherClientId,
                                rm.DateTime
                            )
                        );
                        foreach (var incomingChannel in _incomingChannels)
                        {
                            foreach (var message in messages)
                            {
                                await incomingChannel.Writer.WriteAsync(message);
                            }
                        }
                    }
                );

                _whenReadyTcs.TrySetResult();

                await foreach (var message in _outgoingChannel.Reader.ReadAllAsync(_cts.Token))
                {
                    await hubConnection.SendAsync(
                        nameof(MessageSpecifications.Send),
                        message
                    );
                }
            }
        );
    }

    public ChannelWriter<OutgoingMessage> Writer => _outgoingChannel.Writer;

    public IChannelSubscription<IncomingMessage> Subscribe(Func<IncomingMessage, bool> filter)
    {
        var channel = Channel.CreateBounded<IncomingMessage>(50);

        var result = new ChannelSubscription<IncomingMessage>(
            channel.Reader,
            (_) => _incomingChannels.Remove(channel),
            filter
        );

        _incomingChannels.Add(channel);

        return result;
    }


    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _outgoingChannel.Writer.TryComplete();
        _incomingChannels.ForEach(ch => ch.Writer.TryComplete());

        _cts.Dispose();
    }
}
