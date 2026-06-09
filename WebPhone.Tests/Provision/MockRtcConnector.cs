using EverModern.Blazor.DirectCommunication;
using WebPhone.Contract;

namespace WebPhone.Tests.Provision;

class MockRtcConnector : IRtcConnector
{
    readonly string _id = Random.Shared.Next().ToString();

    WebRtcOffer GenerateOffer() =>
        new("offer", $"sdp-by-{nameof(MockRtcConnector)}-#{_id}-timestamp-{DateTime.UtcNow.Ticks}");

    WebRtcAnswer GenerateAnswer(WebRtcOffer offer) =>
        new(
            "answer",
            $"answer-to-{offer.Sdp}-from-{nameof(MockRtcConnector)}-#{_id}-timestamp-{DateTime.UtcNow.Ticks}"
        );

    public async Task<IRtcConnection?> AcceptConnectionAsync(
        WebRtcOffer offer,
        Func<WebRtcAnswer, Task> sendAnswerBack,
        CancellationToken cancellationToken
    )
    {
        var answer = GenerateAnswer(offer);
        await sendAnswerBack(answer);
        return new MockRtcConnection(offer, answer);
    }

    public async Task<IRtcConnection?> InitiateConnectionAsync(
        Func<WebRtcOffer, Task<WebRtcAnswer?>> getAnswer,
        CancellationToken cancellationToken
    )
    {
        var offer = GenerateOffer();
        var answer = await getAnswer(offer);
        if (answer is null)
        {
            return null;
        }
        return new MockRtcConnection(offer, answer);
    }
}
