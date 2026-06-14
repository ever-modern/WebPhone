using System.Runtime.CompilerServices;
using EverModern.Blazor.DirectCommunication;
using Microsoft.Extensions.Logging;
using WebPhone.Domain;
using WebPhone.Services;
using WebPhone.Services.Channels;
using WebPhone.Tests.Provision;
using Xunit.Abstractions;
using PeerPair=(
    (WebPhone.Services.PeerConnector First, string UserId),
    (WebPhone.Services.PeerConnector Second, string UserId)
    );

namespace WebPhone.Tests;

public class PeerConnectorIntegrationTests(
    TestWebApplicationFactory webApplicationFactory,
    ITestOutputHelper output
) : IntegrationWithBackendTestsSet(webApplicationFactory, output)
{
    async Task<PeerConnector> CreatePeerConnectorAsync(string userId, CancellationToken cancellationToken = default)
    {
        var client = CreateBackendClient(userId);
        var result = new PeerConnector(
            new MockRtcConnector(),
            LoggerFactory.CreateLogger<PeerConnector>($"PeerConnector-user-{userId}"),
            client
        );

        var hub = client.OpenHubConnectionAsync(cancellationToken);

        var channel = new BackendMessagesChannel(hub);

        var incomingReader = channel.Subscribe(m => m.Type is MessageType.ConnectionAttempt);

        await channel.WhenReady;

        _ = Task.Run(async () =>
            {
                using var reader = incomingReader;
                await foreach (var message in reader.ReadAllAsync(default))
                {
                    var (_, _, webRtcOffer, senderClientId, _) = message.SpecifyPayload<WebRtcOffer>()!;
                    await result.ConnectToPeerAsync(
                        senderClientId,
                        default,
                        webRtcOffer
                    );
                }
            }
        );

        await channel.WhenReady.WaitAsync(cancellationToken);

        return result;
    }

    async Task<PeerPair> CreateTwoPeers(CancellationToken cancellationToken = default)
    {
        var user0 = $"User-0";
        var user1 = $"User-1";
        var connector0 = await CreatePeerConnectorAsync(user0, cancellationToken);
        var connector1 = await CreatePeerConnectorAsync(user1, cancellationToken);
        return ((connector0, user0), (connector1, user1));
    }

    async IAsyncEnumerable<(PeerConnector Connector, string PeerId)> GeneratePeers([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < int.MaxValue / 2; i++)
        {
            var user = $"User-{i}";
            yield return (await CreatePeerConnectorAsync(user, cancellationToken), user);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task FirstConnects_SecondOnlyAccepts()
    {
        var ct = Timeout.Token;

        var ((firstConnector, firstUserId), (secondConnector, secondUserId)) = await CreateTwoPeers(ct);
        var firstConnection = await firstConnector.ConnectToPeerAsync(
            secondUserId,
            ct
        );

        var secondConnection = secondConnector.FindReadyConnection(firstUserId);

        Assert.NotNull(firstConnection);
        Assert.NotNull(secondConnection);
    }

    /// <summary>
    /// Verifies that when both peers attempt to connect to each other at the same time,
    /// both connections succeed without deadlock or failure. This tests the race condition
    /// handling in the peer connection logic.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TwoConnectSimultaneously()
    {
        var ct = Timeout.Token;

        var ((firstConnector, firstUserId), (secondConnector, secondUserId)) = await CreateTwoPeers();
        var firstConnectionTask = firstConnector.ConnectToPeerAsync(
            secondUserId,
            ct
        );
        var secondConnectionTask = secondConnector.ConnectToPeerAsync(
            firstUserId,
            ct
        );

        await Task.WhenAll(
            firstConnectionTask,
            secondConnectionTask
        );

        var connectionFirst = await firstConnectionTask;
        var connectionSecond = await secondConnectionTask;

        Assert.NotNull(connectionFirst);
        Assert.NotNull(connectionSecond);
    }

    [Fact(Timeout = 30000)]
    public async Task Connect_All_To_All()
    {
        var peers = new List<(PeerConnector Connector, string PeerId)>();

        const int peersCount = 40;
        await foreach (var item in GeneratePeers(default).Take(peersCount))
            peers.Add(item);

        var ct = Timeout.Token;

        var tasks = peers.SelectMany(peer =>
            {
                var (connector, peerId) = peer;
                var connectionTasks = peers.Where(p => p.PeerId != peerId)
                    .Select((otherPeer) =>
                        {
                            var (otherConnection, otherPeerId) = otherPeer;
                            var connectToPeerAsync = connector.ConnectToPeerAsync(otherPeerId, default);
                            return (peerId, otherPeerId, connectToPeerAsync);
                        }
                    )
                    .ToArray();

                return  connectionTasks;
            }
        ).ToArray();

        await Task.WhenAll(tasks.Select(t=>t.connectToPeerAsync));

        var failedTasks = tasks.Where(t => t.connectToPeerAsync.Result is null).ToArray();

        Assert.Empty(failedTasks);
    }

    [Fact]
    public async Task Connect_Two_Repeatedly_Chaotically()
    {
        var ((firstConnector, firstUserId), (secondConnector, secondUserId)) = await CreateTwoPeers();
        var firstPeerConnections = new List<IRtcConnection?>();
        var secondPeerConnections = new List<IRtcConnection?>();
        var connectionTasks = Enumerable.Range(0, 20)
            .SelectMany(i =>
                {
                    return new Task[]
                    {
                        firstConnector.ConnectToPeerAsync(secondUserId).ContinueWith(t => firstPeerConnections.Add(t.Result)),
                        secondConnector.ConnectToPeerAsync(firstUserId).ContinueWith(t => secondPeerConnections.Add(t.Result))
                    };
                }
            )
            .ToArray();

        await Task.WhenAll(connectionTasks).WaitAsync(Timeout.Token);

        var distinctFirst = firstPeerConnections.Distinct().Count();
        var distinctSecond = secondPeerConnections.Distinct().Count();

        Assert.Equal(1, distinctFirst);
        Assert.Equal(1, distinctSecond);
    }

    [Fact]
    public async Task Connect_FirstConnects_SecondConnects_Again()
    {
        var ct = Timeout.Token;

        var ((firstConnector, firstUserId), (secondConnector, secondUserId)) = await CreateTwoPeers(ct);
        var firstConnection = await firstConnector.ConnectToPeerAsync(
            secondUserId,
            ct
        );

        var secondConnection = secondConnector.FindReadyConnection(firstUserId);

        Assert.NotNull(secondConnection);
        Assert.NotNull(firstConnection);

        var secondConnectionSecondsAttempt = await secondConnector.ConnectToPeerAsync(firstUserId, ct);

        Assert.Same(secondConnection, secondConnectionSecondsAttempt);
    }
}
