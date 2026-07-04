using System.Text.Json;
using WebPhone.Channels;
using WebPhone.Domain;
using WebPhone.Messages;
using WebPhone.Tests.Provision;
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

public class RtcMessageChannelTests
{
    [Fact]
    public async Task WriteReceiveMessage()
    {
        var (connector1, connector2) = (new MockRtcConnector(), new MockRtcConnector());
        var (offer, answer) = (new WebRtcOffer("", ""), new WebRtcAnswer("",""));
        var (connection1, connection2) = (new MockRtcConnection(connector1, offer, answer), new MockRtcConnection(connector2, offer, answer));

        var (channel1, channel2) = (new RtcConnectionMessageChannel(connection1), new RtcConnectionMessageChannel(connection2));

        using var reader1 = channel1.Subscribe(_ => true);
        using var reader2 = channel2.Subscribe(_ => true);

        RtcMessage messageOut = RtcMessage.Create(RtcMessageType.Ping, 203);

        await channel1.Writer.WriteAsync(messageOut);

        var timeout = new CancellationTokenSource(300).Token;

        await foreach (var messageIn in reader2.ReadAllAsync(timeout))
        {
            Assert.Equal(messageOut, messageIn);
            break;
        }
    }
}