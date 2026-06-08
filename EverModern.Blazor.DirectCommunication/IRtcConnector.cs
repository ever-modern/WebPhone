namespace EverModern.Blazor.DirectCommunication;

public interface IRtcConnector
{
    Task<RtcConnection?> AcceptConnectionAsync(
        WebRtcOffer offer,
        Func<WebRtcAnswer, Task> sendAnswerBack,
        CancellationToken cancellationToken
    );
    Task<RtcConnection?> InitiateConnectionAsync(
        Func<WebRtcOffer, Task<WebRtcAnswer?>> getAnswer,
        CancellationToken cancellationToken
    );
}
