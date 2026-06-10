using WebPhone.Domain;

namespace EverModern.Blazor.DirectCommunication;

public interface IRtcConnector
{
    Task<IRtcConnection?> AcceptConnectionAsync(
        WebRtcOffer offer,
        Func<WebRtcAnswer, Task<bool>> sendAnswerBack,
        CancellationToken cancellationToken
    );
    Task<IRtcConnection?> InitiateConnectionAsync(
        Func<WebRtcOffer, Task<WebRtcAnswer?>> getAnswer,
        CancellationToken cancellationToken
    );
}
