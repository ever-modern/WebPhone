using System.Text.Json;
using EverModern.Threading.Channels;
using WebPhone.Backend.Services;
using WebPhone.Backend.Storage;
using WebPhone.Domain;
using Xunit.Abstractions;

namespace WebPhone.Tests;

public class InMemoryMessagesChannel : IMessagesWriter
{
    readonly BroadcastChannel<TransmittedMessage> _inner = new();

    public Task WriteAsync(string targetId, string senderId, MessageContent messageContent, CancellationToken cancellationToken)
    {
        return _inner.WriteAsync(
                message: new(
                    Receiver: targetId,
                    Sender: senderId,
                    Type: messageContent.Type,
                    Payload: messageContent.Payload
                )
            )
            .AsTask();
    }

    public IChannelSubscription<TransmittedMessage> Subscribe(Func<TransmittedMessage, bool> filter) => _inner.Subscribe(filter);
}

public class MatchMakerTests(
    TestWebApplicationFactory webApplicationFactory,
    ITestOutputHelper output
) : IntegrationWithBackendTestsSet(webApplicationFactory, output)
{
    public async Task Bombard()
    {
        var channel = new InMemoryMessagesChannel();
        var store = new RtcNegotiationStore();
        var matchMaker = new RtcMatchMaker(store, CreateLoggerFactory("").CreateLogger<RtcMatchMaker>(""), channel);

        var peers = Enumerable.Range(0, 100)
            .Select(i =>
                {
                    var peerId = $"User-{i}";

                    _ = Task.Run(async () =>
                        {
                            var sub = channel.Subscribe(m => m.Receiver == peerId);
                            await foreach (var message in sub.ReadAllAsync())
                            {
                                var offer = message.Payload.Deserialize<WebRtcOffer>();
                                var senderId = message.Sender;

                                WebRtcAnswer answer = new("answer", $"Answer to offer [{offer}] from {peerId}.");

                                await matchMaker.MatchAsync(
                                    peerId,
                                    senderId,
                                    new(offer, answer),
                                    default
                                );
                            }
                        }
                    );

                    return peerId;
                }
            )
            .ToArray();

        await Task.Delay(50);

        var tasks = peers.SelectMany(peer => peers.Where(otherPeer => otherPeer != peer)
            .Select(otherPeer => matchMaker.MatchAsync(
                    peer,
                    otherPeer,
                    new(new WebRtcOffer("offer", $"offer from {peer} to {otherPeer}"), null),
                    default
                )
            )
        ).ToArray();

        await Task.WhenAll(tasks);
        
        
    }
}
