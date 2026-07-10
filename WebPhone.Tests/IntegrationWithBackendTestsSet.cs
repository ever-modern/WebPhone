using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using WebPhone.Background;
using WebPhone.Channels;
using WebPhone.Tests.Provision;
using Xunit.Abstractions;

namespace WebPhone.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<WebPhone.Api.Program>
{
    public event Action<string> OnLog = _ => { };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(new TestLoggerProvider(log => OnLog.Invoke(log)));
            logging.SetMinimumLevel(LogLevel.Trace);
        });
        OnLog = (_) => { };
    }
}

public abstract class IntegrationWithBackendTestsSet : IAsyncDisposable
{
    readonly TestWebApplicationFactory _webApplicationFactory;
    readonly string _virtualBaseUrl;

    readonly List<(string Message, bool IsServer)> _logs = [];

    int _id0;

    protected IReadOnlyList<(string Message, bool IsServer)> Logs => _logs;

    protected Func<string, TestLoggerProvider> CreateLoggerFactory { get; }

    protected static CancellationTokenSource Timeout =>
        new CancellationTokenSource(
            Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(15)
        );

    protected IntegrationWithBackendTestsSet(ITestOutputHelper output)
    {
        _id0 = Random.Shared.Next(1000);
        _webApplicationFactory = new();
        var client = _webApplicationFactory.CreateClient();
        _virtualBaseUrl = client.BaseAddress!.ToString();
        _webApplicationFactory.OnLog += log =>
        {
            _logs.Add((log, true));
            try
            {
                output.WriteLine($"[SERVER]{log}");
            }
            catch { }
        };
        CreateLoggerFactory = output.ToLoggerFactory(_logs);
    }

    protected BackendClient CreateVirtualBackendClient(string userId)
    {
        var httpMessageHandler = _webApplicationFactory.Server.CreateHandler();
        var client = new BackendClient(
            _virtualBaseUrl,
            new TestProfile(userId),
            httpMessageHandler: httpMessageHandler
        );

        return client;
    }

    protected BackendClient CreateRealBackendClient(string userId, int port = 5194)
    {
        var client = new BackendClient($"http://localhost:{port}", new TestProfile(userId));
        return client;
    }

    public ValueTask DisposeAsync() => _webApplicationFactory.DisposeAsync();

    protected async Task<PeerConnectionsDispatcher> CreatePeerConnectorAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var client = CreateVirtualBackendClient(userId);
        var result = new PeerConnectionsDispatcher(
            new MockRtcConnector(),
            CreateLoggerFactory($"PeerConnector-user-{userId}"),
            client
        );

        var hub = await client.OpenHubConnectionAsync(cancellationToken);

        var channel = await BackendMessagesChannel.BindAsync(hub);

        var handlerLogger = CreateLoggerFactory($"[{userId}]")
            .CreateLogger<IncomingConnectionsHandler>($"IncomingConnectionsHandler-{userId}");
        IncomingConnectionsHandler connectionsHandler = await new IncomingConnectionsHandler(
            peerConnectionsDispatcher: result,
            logger: handlerLogger
        ).StartReadingAsync(channel, default);

        return result;
    }

    protected async IAsyncEnumerable<(
        PeerConnectionsDispatcher Connector,
        string PeerId
    )> GeneratePeers([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (; _id0 < int.MaxValue / 2; _id0++)
        {
            var user = $"User-{_id0}";
            yield return (await CreatePeerConnectorAsync(user, cancellationToken), user);
        }
    }
}
