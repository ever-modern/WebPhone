using EverModern.Blazor.DirectCommunication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using WebPhone.Domain;
using WebPhone.Services;
using WebPhone.Services.Channels;
using WebPhone.Tests.Provision;
using PeerPair = (
    (WebPhone.Services.PeerConnector First, string UserId),
    (WebPhone.Services.PeerConnector Second, string UserId)
);

namespace WebPhone.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<WebPhone.Api.Program>
{
    public List<string> Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(new TestLoggerProvider(Logs));
        });
    }
}

public class PeerConnectorIngegrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _webApplicationFactory;
    readonly string _baseUrl;
    readonly List<string> _serverStd;

    public PeerConnectorIngegrationTests(TestWebApplicationFactory webApplicationFactory)
    {
        _webApplicationFactory = webApplicationFactory;
        var client = _webApplicationFactory.CreateClient();
        _baseUrl = client.BaseAddress.ToString();
        _serverStd = webApplicationFactory.Logs;
    }

    PeerConnector CreatePeerConnector(string userId)
    {
        var client = new BackendConnectionClient(_baseUrl, userId);
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

    PeerPair CreateTwoPeers() => GeneratePeers().Chunk(2).Select(FromArray).First();

    IEnumerable<(PeerConnector Connector, string PeerId)> GeneratePeers()
    {
        for (int i = 0; i < int.MaxValue / 2; i++)
        {
            var user = $"User-{i}";
            yield return (CreatePeerConnector(user), user);
        }
    }

    PeerPair FromArray((PeerConnector Connector, string PeerId)[] array) => (array[0], array[1]);

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

    [Fact]
    public async Task Connect_All_To_All()
    {
        var peers = GeneratePeers().Take(2);

        var tasks = new List<Task<IRtcConnection?>>();

        foreach (var (connector, peerId) in peers)
        {
            foreach (var (otherConnector, otherPeerId) in peers)
            {
                if (peerId != otherPeerId)
                {
                    CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
                    Task<IRtcConnection?> task = connector.ConnectToPeerAsync(
                        otherPeerId,
                        cts.Token
                    );
                    tasks.Add(task);
                }
            }
        }

        await Task.WhenAll(tasks);

        Assert.All(tasks, t => Assert.NotNull(t.Result));
    }
}
