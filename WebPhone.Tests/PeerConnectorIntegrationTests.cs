using System.Runtime.CompilerServices;
using System.Text.Json;
using EverModern.Blazor.DirectCommunication;
using WebPhone.Domain;
using WebPhone.Services;
using WebPhone.Services.Background;
using WebPhone.Services.Channels;
using WebPhone.Tests.Provision;
using Xunit.Abstractions;
using PeerPair=(
    (WebPhone.Services.PeerConnectionsDispatcher First, string UserId),
    (WebPhone.Services.PeerConnectionsDispatcher Second, string UserId)
    );

namespace WebPhone.Tests;

public class PeerConnectorIntegrationTests(
    TestWebApplicationFactory webApplicationFactory,
    ITestOutputHelper output
) : IntegrationWithBackendTestsSet(webApplicationFactory, output)
{
    async Task<PeerConnectionsDispatcher> CreatePeerConnectorAsync(string userId, CancellationToken cancellationToken = default)
    {
        var client = CreateVirtualBackendClient(userId);
        var result = new PeerConnectionsDispatcher(
            new MockRtcConnector(),
            CreateLoggerFactory($"PeerConnector-user-{userId}"),
            client
        );

        var hub = await client.OpenHubConnectionAsync(cancellationToken);

        var channel = await BackendMessagesChannel.BindAsync(hub);

        var handlerLogger = CreateLoggerFactory($"[{userId}]").CreateLogger<IncomingConnectionsHandler>($"IncomingConnectionsHandler-{userId}");
        IncomingConnectionsHandler connectionsHandler =
            await new IncomingConnectionsHandler(peerConnectionsDispatcher: result, logger: handlerLogger)
                .StartReadingAsync(channel, default);

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

    async IAsyncEnumerable<(PeerConnectionsDispatcher Connector, string PeerId)> GeneratePeers([EnumeratorCancellation] CancellationToken cancellationToken = default)
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
        var firstConnection = await firstConnector.ConnectAsync(
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
        var firstConnectionTask = firstConnector.ConnectAsync(
            secondUserId,
            ct
        );
        var secondConnectionTask = secondConnector.ConnectAsync(
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

    [Fact(Timeout = 300_000)]
    public async Task Connect_All_To_All()
    {
        var peers = new List<(PeerConnectionsDispatcher Connector, string PeerId)>();

        const int peersCount = 300;
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
                                var connectToPeerAsync = connector.ConnectAsync(otherPeerId, default).ContinueWith(t =>
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
                }
            )
            .ToArray();

        await Task.WhenAll(tasks.Select(t => t.connectToPeerAsync));

        var failedTasks = tasks.Where(t => t.connectToPeerAsync.Result is null).ToArray();

        var otherSidesSuccesses = failedTasks.Select(ft => tasks.Where(t => t.peerId == ft.otherPeerId && t.otherPeerId == ft.peerId && t.connectToPeerAsync.Result is not null)).ToArray();

        var byPeerLogs = failedTasks.Select(ft => new
                {
                    PeerId = ft.peerId,
                    OtherPeerId = ft.otherPeerId,
                    Logs = Logs.Select(l => (l.IsServer ? "[SERVER]" : "[CLIENT]") + l.Message).Where(l => l.Contains(ft.peerId) && l.Contains(ft.otherPeerId))
                }
            )
            .Select(pairLog => $"***{pairLog.PeerId} -> {pairLog.OtherPeerId}***\n{string.Join('\n', pairLog.Logs)}")
            .ToArray();

        var logsForUser = string.Join("\n\n\n\n", byPeerLogs);

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
                        firstConnector.ConnectAsync(secondUserId).ContinueWith(t => firstPeerConnections.Add(t.Result)),
                        secondConnector.ConnectAsync(firstUserId).ContinueWith(t => secondPeerConnections.Add(t.Result))
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
        var firstConnection = await firstConnector.ConnectAsync(
            secondUserId,
            ct
        );

        var secondConnection = secondConnector.FindReadyConnection(firstUserId);

        Assert.NotNull(secondConnection);
        Assert.NotNull(firstConnection);

        var secondConnectionSecondsAttempt = await secondConnector.ConnectAsync(firstUserId, ct);

        Assert.Same(secondConnection, secondConnectionSecondsAttempt);
    }
}
