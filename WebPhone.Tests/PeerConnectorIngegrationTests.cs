using System.Diagnostics;
using EverModern.Blazor.DirectCommunication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using WebPhone.Domain;
using WebPhone.Services;
using WebPhone.Services.Channels;
using WebPhone.Tests.Provision;
using PeerPair=(
    (WebPhone.Services.PeerConnector First, string UserId),
    (WebPhone.Services.PeerConnector Second, string UserId)
    );

namespace WebPhone.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<WebPhone.Api.Program>
{
    List<string> Logs { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new TestLoggerProvider(Logs));
            }
        );
    }
}

public class PeerConnectorIngegrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _webApplicationFactory;
    readonly string _baseUrl;
    readonly string _testRunPrefix = "";// Guid.NewGuid().ToString("N");
    static CancellationTokenSource Timeout => new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5));


    public PeerConnectorIngegrationTests(TestWebApplicationFactory webApplicationFactory)
    {
        _webApplicationFactory = webApplicationFactory;
        var client = _webApplicationFactory.CreateClient();
        _baseUrl = client.BaseAddress!.ToString();
    }

    PeerConnector CreatePeerConnector(string userId)
    {
        var httpMessageHandler = _webApplicationFactory.Server.CreateHandler();
        var client = new BackendClient(
            _baseUrl,
            new TestProfile(userId),
            httpMessageHandler: httpMessageHandler
        );
        var result = new PeerConnector(
            new MockRtcConnector(),
            new MockLogger<PeerConnector>(),
            client
        );

        var hub = client.OpenHubConnectionAsync();

        var channel = new BackendMessagesChannel(hub);

        var incomingReader = channel.Subscribe(m => m.Type is MessageType.ConnectionAttempt);

        _ = Task.Run(async () =>
            {
                using var reader = incomingReader;
                await foreach (var message in reader.ReadAllAsync())
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

        return result;
    }

    PeerPair CreateTwoPeers() => GeneratePeers()
        .Chunk(2)
        .Select(FromArray)
        .First();

    IEnumerable<(PeerConnector Connector, string PeerId)> GeneratePeers()
    {
        for (int i = 0; i < int.MaxValue / 2; i++)
        {
            var user = $"User-{_testRunPrefix}-{i}";
            yield return (CreatePeerConnector(user), user);
        }
    }

    PeerPair FromArray((PeerConnector Connector, string PeerId)[] array) => (array[0], array[1]);

    [Fact]
    public async Task FirstConnects_SecondOnlyAccepts()
    {
        var ct = Timeout.Token;

        var ((firstConnector, firstUserId), (secondConnector, secondUserId)) = CreateTwoPeers();
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

        var ((firstConnector, firstUserId), (secondConnector, secondUserId)) = CreateTwoPeers();
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
        var peers = GeneratePeers()
            .Take(50);

        List<Task<IRtcConnection?>> tasks = [];

        foreach (var (connector, peerId) in peers)
        {
            foreach (var (otherConnector, otherPeerId) in peers)
            {
                if (peerId == otherPeerId)
                    continue;

                CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
                var task = connector.ConnectToPeerAsync(
                    otherPeerId,
                    cts.Token
                );
                tasks.Add(task);
            }
        }

        await Task.WhenAll(tasks);

        Assert.All(
            tasks,
            t => Assert.NotNull(t.Result)
        );
    }
}
