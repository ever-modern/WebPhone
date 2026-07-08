using Xunit.Abstractions;
using Xunit.Sdk;

namespace WebPhone.Tests;

[Collection(nameof(IntegrationTestCollection))]
public class BackendCommunicationTests(
    ITestOutputHelper output
) : IntegrationWithBackendTestsSet(output)
{
    const string _userId = "Single-User";

    [Fact(Timeout = 1000)]
    public async Task SendRequestAsync()
    {
        var ct = Timeout.Token;

        var client = CreateVirtualBackendClient(_userId);

        var result = await client.GetChatMessagesAsync(Guid.NewGuid().ToString(), 0, ct);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact(Timeout = 1000)]
    public async Task EstablishSignalRConnectionAsync()
    {
        var ct = Timeout.Token;

        var client = CreateVirtualBackendClient(_userId);

        var connection = await client.OpenHubConnectionAsync(ct);

        Assert.NotNull(connection);
    }
}
