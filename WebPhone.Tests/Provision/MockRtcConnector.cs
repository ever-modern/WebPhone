using EverModern.Blazor.DirectCommunication;
using WebPhone.Domain;

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

    public async Task<IRtcConnection?> AcceptConnectionAsync(WebRtcOffer offer, Func<WebRtcAnswer, Task<String>> sendAnswerBack, CancellationToken cancellationToken)
    {
        var answer = GenerateAnswer(offer);
        var couldSendAnswer = await sendAnswerBack(answer) is not null or "";
        if (couldSendAnswer is false)
        {
            return null;
        }

        return new MockRtcConnection(this, offer, answer);
    }

    public async Task<IRtcConnection?> InitiateConnectionAsync(Func<WebRtcOffer, Task<(WebRtcAnswer? Answer, String? ConnectionId)>> getAnswer, CancellationToken cancellationToken)
    {
        var offer = GenerateOffer();
        var (answer, connectionId) = await getAnswer(offer);
        if (answer is null)
        {
            return null;
        }
        return new MockRtcConnection(this, offer, answer);
    }

    public ValueTask CloseConnectionAsync(IRtcConnection connection) => throw new NotImplementedException();
}
