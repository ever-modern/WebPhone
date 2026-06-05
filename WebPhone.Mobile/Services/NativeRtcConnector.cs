using EverModern.Blazor.DirectCommunication;

namespace WebPhone.Android.Services;

public class NativeRtcConnector() : IRtcConnector
{
    public Task<RtcConnection> AcceptConnectionAsync(WebRtcOffer offer, Func<WebRtcAnswer, Task> sendAnswerBack)
    {
        throw new NotImplementedException();
    }

    public Task<RtcConnection> InitiateConnectionAsync(Func<WebRtcOffer, Task<WebRtcAnswer>> getAnswer)
    {
        throw new NotImplementedException();
    }
}
