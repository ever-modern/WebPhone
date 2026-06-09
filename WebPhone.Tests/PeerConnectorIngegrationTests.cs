using WebPhone.Contract;
using WebPhone.Messages;
using WebPhone.Services;
using WebPhone.Services.Channels;
using WebPhone.Tests.Provision;

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

        var channel = new BackendMessagesChannel(client, 50).Start();

        _ = Task.Run(async () =>
        {
            using var reader = channel.Subscribe(m => m.Type is MessageType.ConnectionAttempt);
            await foreach (var message in reader.ReadAllAsync())
            {
                var specificMessage = message.SpecifyPayload<WebRtcOffer>()!;
                var __ = result.HandleIncomingConnectionRequestAsync(
                    specificMessage.SenderClientId,
                    specificMessage.Payload
                );
            }
        });

        return result;
    }

    static (
        (PeerConnector First, string UserId),
        (PeerConnector Second, string UserId)
    ) CreateTwoPeers()
    {
        string firstUserId = $"First-{Guid.NewGuid()}";
        string secondUserId = $"Second-{Guid.NewGuid()}";
        return (
            (CreatePeerConnector(firstUserId), firstUserId),
            (CreatePeerConnector(secondUserId), secondUserId)
        );
    }

    [Fact]
    public async Task TestSimpleAsync()
    {
        var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = timeoutCts.Token;

        var ((firstConnector, firstUserId), (secondConnector, secondUserId)) = CreateTwoPeers();
        var connectionFirst = await firstConnector.GetPeerConnectionAsync(secondUserId, ct);
        var connectionSecond = await secondConnector.GetPeerConnectionAsync(firstUserId, ct);

        Assert.NotNull(connectionFirst);
        Assert.NotNull(connectionSecond);
    }
}
