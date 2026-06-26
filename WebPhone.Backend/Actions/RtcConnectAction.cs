using WebPhone.Backend.Services;
using Microsoft.Extensions.Logging;
using WebPhone.Domain;

namespace WebPhone.Backend.Actions;

public class RtcConnectAction(
    RtcMatchMaker rtcMatchMaker,
    RequestSupplements requestSupplements,
    ILogger<RtcConnectAction> logger
)
    : ApiActionConcrete<RtcConnectionRequest, RtcMatchParameters>
{
    public override string Route => "rtc-connect";

    public override Task<RtcMatchParameters> ExecuteAsync(
        RtcConnectionRequest input,
        CancellationToken cancellationToken = default
    )
    {
        var clientId = requestSupplements.RequireClientId();
        logger.LogInformation(
            "[RTC] rtc-connect request {ClientId} -> {TargetId}. OfferPresent={OfferPresent}, AnswerPresent={AnswerPresent}",
            clientId,
            input.TargetId,
            input.Offer is not null,
            input.Answer is not null
        );

        var result = rtcMatchMaker.MatchAsync(
            clientId,
            input.TargetId,
            new(input.Offer, input.Answer),
            cancellationToken
        );

        return result;
    }
}
