using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using WebPhone.Services;
using WebPhone.Services.Data;
using WebPhone.Tests.Provision;
using Xunit.Abstractions;

namespace WebPhone.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<WebPhone.Api.Program>
{
    public event Action<string> OnLog = _ => {};

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
            {                
                logging.ClearProviders();
                logging.AddProvider(new TestLoggerProvider(log => OnLog.Invoke(log)));
                logging.SetMinimumLevel(LogLevel.Trace);
            }
        );
        OnLog = (_) => {};
    }
}

public abstract class IntegrationWithBackendTestsSet : IClassFixture<TestWebApplicationFactory>
{
    readonly TestWebApplicationFactory _webApplicationFactory;
    readonly string _virtualBaseUrl;
    protected IEnumerable<string> ServerLogs => Logs.Where(l => l.IsServer).Select(l => l.Message);
    protected IEnumerable<string> ClientLogs => Logs.Where(l => l.IsServer == false).Select(l => l.Message);

    readonly List<(string Message, bool IsServer)> _logs = [];

    protected IReadOnlyList<(string Message, bool IsServer)> Logs => _logs;

    protected Func<string, TestLoggerProvider> CreateLoggerFactory { get; }

    protected static CancellationTokenSource Timeout => new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(2));

    protected IntegrationWithBackendTestsSet(TestWebApplicationFactory webApplicationFactory, ITestOutputHelper output)
    {
        _webApplicationFactory = webApplicationFactory;
        var client = _webApplicationFactory.CreateClient();
        _virtualBaseUrl = client.BaseAddress!.ToString();
        _webApplicationFactory.OnLog += log =>
        {
            _logs.Add((log, true));
            try { output.WriteLine($"[SERVER]{log}"); }
            catch {}
        };
        CreateLoggerFactory = prefix => new TestLoggerProvider(log =>
            {
                var logWithPrefix = $"[{prefix}]:{log}";
                output.WriteLine(logWithPrefix);
                _logs.Add((logWithPrefix, false));
            }
        );
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
}
