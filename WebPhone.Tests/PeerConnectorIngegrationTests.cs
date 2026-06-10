using WebPhone.Domain;
using WebPhone.Messages;
using WebPhone.Services;
using WebPhone.Services.Channels;
using WebPhone.Tests.Provision;
using PeerPair = (
    (WebPhone.Services.PeerConnector First, string UserId),
    (WebPhone.Services.PeerConnector Second, string UserId)
);

namespace WebPhone.Tests;

public class PeerConnectorIngegrationTests
{
    static PeerConnector CreatePeerConnector(string userId)
    {
        var client = new BackendConnectionClient("http://localhost:5194", userId);
        var result = new PeerConnector(
            new MockRtcConnector(),
            new MockLogger<PeerConnector>(),
            client
        );

        var channel = new BackendMessagesChannel(client, 50);

        var incomingReader = channel.Subscribe(m => m.Type is MessageType.ConnectionAttempt);
        channel.Start();

        _ = Task.Run(async () =>
        {
            using var reader = incomingReader;
            await foreach (var message in reader.ReadAllAsync())
            {
                var specificMessage = message.SpecifyPayload<WebRtcOffer>()!;
                await result.ConnectToPeerAsync(
                    specificMessage.SenderClientId,
                    default,
                    specificMessage.Payload
                );
            }
        });

        return result;
    }

    static PeerPair CreateTwoPeers() => GeneratePeers().Chunk(2).Select(FromArray).First();

    static IEnumerable<(PeerConnector Connector, string PeerId)> GeneratePeers()
    {
        for (int i = 0; i < int.MaxValue / 2; i++)
        {
            var user = $"User-{i}";
            yield return (CreatePeerConnector(user), user);
        }
    }

    static PeerPair FromArray((PeerConnector Connector, string PeerId)[] array) =>
        (array[0], array[1]);

    [Fact]
    public async Task FirstConnects_SecondOnlyAccepts()
    {
        var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var ct = timeoutCts.Token;

        var ((firstConnector, firstUserId), (secondConnector, secondUserId)) = CreateTwoPeers();
        var firstConnection = await firstConnector.ConnectToPeerAsync(secondUserId, ct);

        var secondConnection = secondConnector.FindReadyConnection(firstUserId);

        Assert.NotNull(firstConnection);
        Assert.NotNull(secondConnection);
    }

    [Fact]
    public async Task TwoConnectSimultaneously()
    {
        var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var ct = timeoutCts.Token;

        var ((firstConnector, firstUserId), (secondConnector, secondUserId)) = CreateTwoPeers();
        var firstConnectionTask = firstConnector.ConnectToPeerAsync(secondUserId, ct);
        var secondConnectionTask = secondConnector.ConnectToPeerAsync(firstUserId, ct);

        await Task.WhenAll(firstConnectionTask, secondConnectionTask);

        var connectionFirst = await firstConnectionTask;
        var connectionSecond = await secondConnectionTask;

        Assert.NotNull(connectionFirst);
        Assert.NotNull(connectionSecond);
    }
}
