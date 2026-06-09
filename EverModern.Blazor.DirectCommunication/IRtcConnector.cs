using WebPhone.Contract;

namespace EverModern.Blazor.DirectCommunication;

public interface IRtcConnector
{
    Task<IRtcConnection?> AcceptConnectionAsync(
        WebRtcOffer offer,
        Func<WebRtcAnswer, Task> sendAnswerBack,
        CancellationToken cancellationToken
    );
    Task<IRtcConnection?> InitiateConnectionAsync(
        Func<WebRtcOffer, Task<WebRtcAnswer?>> getAnswer,
        CancellationToken cancellationToken
    );
}
