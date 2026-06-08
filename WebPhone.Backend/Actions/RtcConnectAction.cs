using WebPhone.Backend.Services;
using WebPhone.Contract;

namespace WebPhone.Backend.Actions;

public class RtcConnectAction(RtcMatchMaker rtcMatchMaker, RequestSupplements requestSupplements)
    : ApiActionConcrete<RtcConnectionRequest, RtcMatchParameter>
{
    public override string Route => "rtc-connect";

    public override Task<RtcMatchParameter> ExecuteAsync(
        RtcConnectionRequest input,
        CancellationToken cancellationToken = default
    )
    {
        var clientId = requestSupplements.RequireClientId();
        var result = rtcMatchMaker.MatchAsync(
            clientId,
            input.TargetId,
            new(input.Offer, input.Answer),
            cancellationToken
        );
        return result;
    }
}
