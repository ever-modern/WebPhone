using System.Text.Json;
using EverModern.Threading.Locks;
using Microsoft.Extensions.Logging;
using WebPhone.Backend.Storage;
using WebPhone.Domain;

namespace WebPhone.Backend.Services;

public class RtcMatchMaker(
    RtcNegotiationStore currentNegotiations,
    ILogger<RtcMatchMaker> logger,
    IMessagesWriter messagesWriter
)
{
    static readonly TimeSpan OfferTimeout = TimeSpan.FromSeconds(30);

    public async Task<RtcMatchResponse> MatchAsync(
        string initiatorId,
        string targetId,
        RtcMatchParameters parameters,
        CancellationToken cancellationToken
    )
    {
        var (offer, answer) = parameters;

        if (offer is null)
        {
            throw new UserFaultException("No offer provided");
        }

        var pair = new PeersPair(initiatorId, targetId);

        var isNew = false;
        OngoingNegotiation ongoing;

        using (
            var negotiationEntry = currentNegotiations.Acquire(
                pair,
                p =>
                {
                    isNew = true;
                    return StartNegotiation(offer, OfferTimeout);
                }
            )
        )
        {
            ongoing = negotiationEntry.Value;

            logger.LogInformation(
                "{initiatorId} trying to connect to {targetId}",
                initiatorId,
                targetId
            );

            if (isNew == false)
            {
                logger.LogInformation(
                    "There is already proceeding negotiation for pair {pair}",
                    pair
                );
                if (offer != ongoing.Offer)
                {
                    logger.LogInformation("Countering incoming offer.");
                    ongoing.CompleteWithCounterOffer(offer);
                    negotiationEntry.Remove();
                    return new(offer, null);
                }
                if (answer is null)
                {
                    logger.LogWarning(
                        "{initiatorId} tried to connect without an answer",
                        initiatorId
                    );
                    throw new UserFaultException("No answer provided");
                }

                var result = new RtcMatchResponse(offer, answer);
                var offerCut = new string([.. offer.Sdp.Take(60)]) + "...";
                var answerCut = new string([.. answer.Sdp.Take(60)]) + "...";
                logger.LogInformation(
                    "Completing negotion {from} -> {to} with offer:'{offerCut}' answer:'{answerCut}'. The connection id: {connectionId}",
                    initiatorId,
                    targetId,
                    offerCut,
                    answerCut,
                    result.Id
                );
                ongoing.Complete(answer);
                negotiationEntry.Remove();
                logger.LogInformation("Removing pair {pair} negotiation from store.", pair);
                return result;
            }
        } // lock released before any await — prevents deadlock

        logger.LogInformation(
            "{initiatorId} started negotiation with {targetId}",
            initiatorId,
            targetId
        );

        try
        {
            await NotifyTargetPeerAsync(initiatorId, targetId, offer, cancellationToken);

            var answerFromPeer = await ongoing.WhenCompleted;

            return new(answerFromPeer.Offer, answerFromPeer.Answer);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("The pair {pair} negotiation timed out.", pair);
            throw new UserFaultException(
                $"Negotiation hasn't been completed within the time boundary of {OfferTimeout}."
            );
        }
    }

    async Task NotifyTargetPeerAsync(
        string initiatorId,
        string targetId,
        WebRtcOffer offer,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await messagesWriter.WriteAsync(
                targetId: targetId,
                senderId: initiatorId,
                messageContent: new(
                    Type: MessageType.ConnectionAttempt,
                    Payload: JsonSerializer.SerializeToElement(value: offer)
                ),
                cancellationToken: cancellationToken
            );

            logger.LogInformation(
                message: "[RTC] Pushed ConnectionAttempt to {TargetId} via SignalR",
                args: targetId
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                exception: ex,
                message: "[RTC] Failed to push ConnectionAttempt to {TargetId} via SignalR (peer may not be connected to hub). Falling back to DB storage.",
                args: targetId
            );
        }
    }

    static OngoingNegotiation StartNegotiation(WebRtcOffer offer, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<RtcMatchParameters>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetCanceled());
        OngoingNegotiation result = new(tcs, offer);
        return result;
    }
}
