using System.Text.Json;
using EverModern.Events;
using EverModern.Threading.Channels;
using WebPhone.Backend.Services;
using WebPhone.Backend.Storage;
using WebPhone.Domain;
using Xunit.Abstractions;

namespace WebPhone.Tests;

public class InMemoryMessagesChannel : IMessagesWriter
{
    readonly EventSource<TransmittedMessage> _inner = new();

    public Task WriteAsync(
        string targetId,
        string senderId,
        MessageContent messageContent,
        CancellationToken cancellationToken
    )
    {
        TransmittedMessage message = new(
            Receiver: targetId,
            Sender: senderId,
            Type: messageContent.Type,
            Payload: messageContent.Payload
        );

        _inner.Invoke(message);

        return Task.CompletedTask;
    }

    public INotifier<TransmittedMessage> Event => _inner;
}

public class MatchMakerTests(ITestOutputHelper output)
{
    static WebRtcOffer CreateOffer(string from, string to) =>
        new("offer", $"Offer from {from} to {to} id:{CommonIdsGenerator.NewId()}");

    static WebRtcAnswer CreateAnswer(string from, string to) =>
        new("answer", $"Answer from {from} to {to} id:{CommonIdsGenerator.NewId()}");

    [Fact]
    public async Task Bombard_One_Another()
    {
        var channel = new InMemoryMessagesChannel();
        var store = new RtcNegotiationStore();
        var matchMaker = new RtcMatchMaker(
            store,
            output.ToLoggerFactory()("").CreateLogger<RtcMatchMaker>(""),
            channel
        );

        List<RtcMatchParameters> unansweredRequests = [];

        List<RtcMatchResponse> responses = [];

        const string peer1 = "User-1";
        const string peer2 = "User-2";

        using var _ = channel.Event.Subscribe(
            (TransmittedMessage message) =>
                Task.Run(async () =>
                {
                    var answer = CreateAnswer(message.Receiver, message.Sender);
                    var offer = JsonSerializer.Deserialize<WebRtcOffer>(message.Payload);
                    var request = new RtcMatchParameters(offer, answer);
                    unansweredRequests.Add(request);
                    var response = await matchMaker.MatchAsync(
                        message.Receiver,
                        message.Sender,
                        request,
                        default
                    );
                    responses.Add(response);
                    unansweredRequests.Remove(request);
                    return response;
                })
        );

        var start = (string from, string to, int howMany) =>
            Enumerable
                .Range(0, howMany)
                .Select(_ =>
                    Task.Run(async () =>
                    {
                        WebRtcOffer offer = CreateOffer(from, to);
                        var response = await matchMaker.MatchAsync(
                            from,
                            to,
                            new RtcMatchParameters(offer, null),
                            default
                        );

                        responses.Add(response);

                        if (response.Id is not null)
                        {
                            return;
                        }

                        if (response.Answer is null && response.Offer != offer)
                        {
                            var answerToCounter = CreateAnswer(from, to);
                            var counterAnswerAttempt = await matchMaker.MatchAsync(
                                from,
                                to,
                                new RtcMatchParameters(response.Offer, answerToCounter),
                                default
                            );
                            responses.Add(counterAnswerAttempt);
                        }
                    })
                )
                .ToArray();

        const int numberOfRequests = 100;

        await Task.WhenAll([
            ..start(peer1, peer2, numberOfRequests),
            ..start(peer2, peer1, numberOfRequests),
        ]);

        await Task.Delay(500);

        var connectionsMade = responses.Count(r => r.Id is not null);
        const int expectedNumberOfConnection = numberOfRequests * 2;

        Assert.Empty(unansweredRequests);
        Assert.True(connectionsMade >= expectedNumberOfConnection);
    }
}
