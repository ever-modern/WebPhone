using System.Collections.Concurrent;
using EverModern.Blazor.DirectCommunication;
using WebPhone.Domain;

namespace WebPhone.Tests.Provision;

class MockRtcConnector : IRtcConnector
{
    readonly string _id = Random.Shared.Next().ToString();

    readonly ConcurrentDictionary<WebRtcOffer, TaskCompletionSource<WebRtcAnswer>> _answerTasks =
        new();

    WebRtcOffer GenerateOffer() =>
        new("offer", $"sdp-by-{nameof(MockRtcConnector)}-#{_id}-timestamp-{DateTime.UtcNow.Ticks}");

    WebRtcAnswer GenerateAnswer(WebRtcOffer offer) =>
        new(
            "answer",
            $"answer-to-{offer.Sdp}-from-{nameof(MockRtcConnector)}-#{_id}-timestamp-{DateTime.UtcNow.Ticks}"
        );

    public async Task<IRtcConnection?> AcceptConnectionAsync(
        WebRtcOffer offer,
        Func<WebRtcAnswer, Task<string>> sendAnswerBack,
        CancellationToken cancellationToken
    )
    {
        var answer = GenerateAnswer(offer);
        var connectionId = await sendAnswerBack(answer);
        if (string.IsNullOrEmpty(connectionId))
        {
            return null;
        }

        var result = new MockRtcConnection(this, offer, answer, connectionId);

        await result.WhenConnected;

        return result;
    }

    public async Task<IRtcConnection?> InitiateConnectionAsync(
        Func<WebRtcOffer, Task<(WebRtcAnswer? Answer, string? ConnectionId)>> getAnswer,
        CancellationToken cancellationToken
    )
    {
        var offer = GenerateOffer();

        var (answer, connectionId) = await getAnswer(offer);

        if (answer is null)
        {
            return null;
        }

        var result = new MockRtcConnection(this, offer, answer, connectionId);

        await result.WhenConnected;

        return result;
    }

    public ValueTask CloseConnectionAsync(IRtcConnection connection)
    {
        if (connection is MockRtcConnection mock)
            mock.Close();
        return ValueTask.CompletedTask;
    }
}
