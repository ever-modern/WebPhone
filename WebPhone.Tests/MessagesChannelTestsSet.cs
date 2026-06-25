using System.Text.Json;
using WebPhone.Channels;
using WebPhone.Domain;
using WebPhone.Messages;
using Xunit.Abstractions;

namespace WebPhone.Tests;

public class MessagesChannelTestsSet(
    ITestOutputHelper output
) : IntegrationWithBackendTestsSet(output)
{
    const string _userId = "Single-User";

    [Fact]
    public async Task SendMessage_ToSelf_ReceiveMessage()
    {
        var ct = Timeout.Token;

        var client = CreateVirtualBackendClient(_userId);
        var hub = await client.OpenHubConnectionAsync(ct);

        var channel = await BackendMessagesChannel.BindAsync(hub);

        OutgoingMessage<int> messageOut = new(MessageType.Signal, 203, _userId);

        using var reader = channel.Subscribe(_ => true);

        await channel.Writer.WriteAsync(messageOut, ct);

        var messageIn = await reader.ReadAllAsync(ct).FirstAsync(ct);
        var messageSpecific = messageIn.SpecifyPayload<int>();

        Assert.NotNull(messageSpecific);
        Assert.Equal(203, messageSpecific.Payload);
        Assert.Equal(_userId, messageSpecific.SenderClientId);
        Assert.Equal(MessageType.Signal, messageSpecific.Type);
    }
}
