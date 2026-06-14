using Xunit.Abstractions;
using Xunit.Sdk;

namespace WebPhone.Tests;

public class BackendCommunicationTests : IntegrationWithBackendTestsSet
{
    const string _userId = "Single-User";

    public BackendCommunicationTests(TestWebApplicationFactory webApplicationFactory, ITestOutputHelper output) : base(webApplicationFactory, output) {}
    [Fact(Timeout = 1000)]
    public async Task SendRequestAsync()
    {
        var ct = Timeout.Token;

        var client = CreateBackendClient(_userId);

        var result = await client.GetChatMessagesAsync(Guid.NewGuid().ToString(), 0, ct);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact(Timeout = 1000)]
    public async Task EstablishSignalRConnectionAsync()
    {
        var ct = Timeout.Token;

        var client = CreateBackendClient(_userId);

        var connection = await client.OpenHubConnectionAsync(ct);

        Assert.NotNull(connection);
    }
}
