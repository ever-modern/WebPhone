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
    public List<string> Logs { get; } = [];

    public event Action<string> OnLog = Console.WriteLine;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new TestLoggerProvider(log => OnLog.Invoke(log)));
            }
        );
    }
}

public abstract class IntegrationWithBackendTestsSet : IClassFixture<TestWebApplicationFactory>
{
    readonly TestWebApplicationFactory _webApplicationFactory;
    readonly string _virtualBaseUrl;
    protected readonly List<string> ServerLogs;

    protected TestLoggerProvider LoggerFactory { get; }

    protected static CancellationTokenSource Timeout => new CancellationTokenSource(Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(2));

    protected IntegrationWithBackendTestsSet(TestWebApplicationFactory webApplicationFactory, ITestOutputHelper output)
    {
        _webApplicationFactory = webApplicationFactory;
        var client = _webApplicationFactory.CreateClient();
        _virtualBaseUrl = client.BaseAddress!.ToString();
        ServerLogs = webApplicationFactory.Logs;
        _webApplicationFactory.OnLog += log =>
        {
            try { output.WriteLine($"[SERVER]{log}"); }
            catch {}
        };
        LoggerFactory = new TestLoggerProvider(output.WriteLine);
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
