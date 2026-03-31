namespace WebPhone.Services;

public class PhoneFactory(WebRtcInterop webRtc,
    IJSRuntime jsRuntime,
    ILoggerFactory loggerFactory,
    IOptions<PhoneOptions> options,
    IMessagesChannel externalChannel,
    RtcConnector rtcConnector)
{
    public Phone Create(User userInfo)
    {
        return new Phone(webRtc, jsRuntime, loggerFactory.CreateLogger<Phone>(), options.Value, externalChannel, rtcConnector, userInfo);
    }
}
