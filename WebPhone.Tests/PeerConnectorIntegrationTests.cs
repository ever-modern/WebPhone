using EverModern.Blazor.DirectCommunication;
using Xunit.Abstractions;
using PeerPair = (
    (WebPhone.PeerConnectionsDispatcher First, string UserId),
    (WebPhone.PeerConnectionsDispatcher Second, string UserId)
);

namespace WebPhone.Tests;

[Collection(nameof(IntegrationTestCollection))]
public class PeerConnectorIntegrationTests(ITestOutputHelper output)
    : IntegrationWithBackendTestsSet(output)
{
    async Task<PeerPair> CreateTwoPeers(CancellationToken cancellationToken = default)
    {
        var user0 = $"User-0";
        var user1 = $"User-1";
        var connector0 = await CreatePeerConnectorAsync(user0, cancellationToken);
        var connector1 = await CreatePeerConnectorAsync(user1, cancellationToken);
        return ((connector0, user0), (connector1, user1));
    }

    [Fact(Timeout = 30000)]
    public async Task FirstConnects_SecondOnlyAccepts()
    {
        var ct = Timeout.Token;

        var ((firstConnector, firstUserId), (secondConnector, secondUserId)) = await CreateTwoPeers(
            ct
        );
        var firstConnection = await firstConnector.ConnectAsync(secondUserId, ct);

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

        var ((firstConnector, firstUserId), (secondConnector, secondUserId)) =
            await CreateTwoPeers();
        var firstConnectionTask = firstConnector.ConnectAsync(secondUserId, ct);
        var secondConnectionTask = secondConnector.ConnectAsync(firstUserId, ct);

        await Task.WhenAll(firstConnectionTask, secondConnectionTask);

        var connectionFirst = await firstConnectionTask;
        var connectionSecond = await secondConnectionTask;

        Assert.NotNull(connectionFirst);
        Assert.NotNull(connectionSecond);
    }

    [Fact(Timeout = 30_000)]
    public async Task Connect_All_To_All()
    {
        var peers = new List<(PeerConnectionsDispatcher Connector, string PeerId)>();

        const int peersCount = 200;
        await foreach (var item in GeneratePeers(default).Take(peersCount))
            peers.Add(item);

        var ct = Timeout.Token;

        var tasks = peers
            .SelectMany(peer =>
            {
                var (connector, peerId) = peer;
                var connectionTasks = peers
                    .Where(p => p.PeerId != peerId)
                    .Select(
                        (otherPeer) =>
                        {
                            var (otherConnection, otherPeerId) = otherPeer;
                            var connectToPeerAsync = connector
                                .ConnectAsync(otherPeerId, default)
                                .ContinueWith(t =>
                                {
                                    if (t.IsCompletedSuccessfully == false)
                                        return null;

                                    return t.Result;
                                });
                            return (peerId, otherPeerId, connectToPeerAsync);
                        }
                    )
                    .ToArray();

                return connectionTasks;
            })
            .ToArray();

        await Task.WhenAll(tasks.Select(t => t.connectToPeerAsync));

        var failedTasks = tasks.Where(t => t.connectToPeerAsync.Result is null).ToArray();

        var otherSidesSuccesses = failedTasks
            .Select(ft =>
                tasks.Where(t =>
                    t.peerId == ft.otherPeerId
                    && t.otherPeerId == ft.peerId
                    && t.connectToPeerAsync.Result is not null
                )
            )
            .ToArray();

        var byPeerLogs = failedTasks
            .Select(ft => new
            {
                PeerId = ft.peerId,
                OtherPeerId = ft.otherPeerId,
                Logs = Logs.Select(l => (l.IsServer ? "[SERVER]" : "[CLIENT]") + l.Message)
                    .Where(l => l.Contains(ft.peerId) && l.Contains(ft.otherPeerId)),
            })
            .Select(pairLog =>
                $"***{pairLog.PeerId} -> {pairLog.OtherPeerId}***\n{string.Join('\n', pairLog.Logs)}"
            )
            .ToArray();

        var logsForUser = string.Join("\n\n\n\n", byPeerLogs);

        Assert.Empty(failedTasks);
    }

    [Fact(Timeout = 30_000)]
    public async Task Connect_Two_Repeatedly_Chaotically()
    {
        var ct = Timeout.Token;
        var ((firstConnector, firstUserId), (secondConnector, secondUserId)) =
            await CreateTwoPeers();
        var firstPeerConnections = new List<IRtcConnection?>();
        var secondPeerConnections = new List<IRtcConnection?>();

        for (int i = 0; i < 5; i++)
        {
            firstPeerConnections.Add(
                await firstConnector.ConnectAsync(secondUserId, ct)
            );
            secondPeerConnections.Add(
                secondConnector.FindReadyConnection(firstUserId)
            );
        }

        Assert.All(firstPeerConnections, Assert.NotNull);
        Assert.All(secondPeerConnections, Assert.NotNull);

        var distinctFirst = firstPeerConnections.Distinct().Count();
        var distinctSecond = secondPeerConnections.Distinct().Count();

        output.WriteLine($"Distinct first peer connections: {distinctFirst}");
        output.WriteLine($"Distinct second peer connections: {distinctSecond}");
    }

    [Fact]
    public async Task Connect_FirstConnects_SecondConnects_Again()
    {
        var ct = Timeout.Token;

        var ((firstConnector, firstUserId), (secondConnector, secondUserId)) = await CreateTwoPeers(
            ct
        );
        var firstConnection = await firstConnector.ConnectAsync(secondUserId, ct);

        var secondConnection = secondConnector.FindReadyConnection(firstUserId);

        Assert.NotNull(secondConnection);
        Assert.NotNull(firstConnection);

        var secondConnectionSecondsAttempt = await secondConnector.ConnectAsync(firstUserId, ct);

        Assert.Same(secondConnection, secondConnectionSecondsAttempt);
    }

    [Fact(Timeout = 30_000)]
    public async Task Connect_Close_BothConnectionsAreClosed_ThenReconnectSuccessfully()
    {
        var ct = Timeout.Token;

        var ((firstConnector, firstUserId), (secondConnector, secondUserId)) = await CreateTwoPeers(
            ct
        );
        var firstConnection = await firstConnector.ConnectAsync(secondUserId, ct);

        var secondConnection = secondConnector.FindReadyConnection(firstUserId);

        Assert.NotNull(firstConnection);
        Assert.NotNull(secondConnection);

        await firstConnector.DisconnectFromPeerAsync(secondUserId, ct);

        firstConnection = firstConnector.FindReadyConnection(secondUserId);
        secondConnection = secondConnector.FindReadyConnection(firstUserId);

        Assert.Null(firstConnection);
        Assert.Null(secondConnection);

        firstConnection = await firstConnector.ConnectAsync(secondUserId, ct);
        secondConnection = secondConnector.FindReadyConnection(firstUserId);

        Assert.NotNull(firstConnection);
        Assert.NotNull(secondConnection);
    }
}
