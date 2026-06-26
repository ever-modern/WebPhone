global using RtcConnectionId=string;
using WebPhone.Domain;

namespace EverModern.Blazor.DirectCommunication;

public interface IRtcConnector
{
    Task<IRtcConnection?> AcceptConnectionAsync(
        WebRtcOffer offer,
        Func<WebRtcAnswer, Task<RtcConnectionId>> sendAnswerBack,
        CancellationToken cancellationToken
    );
    Task<IRtcConnection?> InitiateConnectionAsync(
        Func<WebRtcOffer, Task<(WebRtcAnswer? Answer, RtcConnectionId? ConnectionId)>> getAnswer,
        CancellationToken cancellationToken
    );

    ValueTask CloseConnectionAsync(IRtcConnection connection);
}
