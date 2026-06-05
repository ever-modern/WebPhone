namespace EverModern.Blazor.DirectCommunication;

public interface IRtcConnector
{
    Task<RtcConnection> AcceptConnectionAsync(WebRtcOffer offer, Func<WebRtcAnswer, Task> sendAnswerBack);
    Task<RtcConnection> InitiateConnectionAsync(Func<WebRtcOffer, Task<WebRtcAnswer>> getAnswer);
}