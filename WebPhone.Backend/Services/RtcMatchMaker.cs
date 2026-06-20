using System.Text.Json;
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

    public async Task<RtcMatchParameter> MatchAsync(
        string initiatorId,
        string targetId,
        RtcMatchParameter parameters,
        CancellationToken cancellationToken
    )
    {
        var (offer, answer) = parameters;

        if (offer is null)
        {
            throw new UserFaultException("No offer provided");
        }

        var pair = new PeersPair(initiatorId, targetId);

        bool sendOffer = false;

        var isNew = false;

        using var negotiationEntry = currentNegotiations.Acquire(
            pair,
            p =>
            {
                isNew = true;
                return StartNegotiation(offer, OfferTimeout);
            }
        );

        logger.LogDebug("{initiatorId} trying to connect to {targetId}", initiatorId, targetId);
        
        if (isNew == false)
        {
            logger.LogDebug("There is already proceeding negotiation for pair {pair}", pair);
            var request = negotiationEntry.Value;
            if (offer != request.Offer)
            {
                logger.LogDebug("Countering incoming offer.");
                request.ReplaceOffer(request.Offer);
                return new(request.Offer, null);
            }
            if (answer is null)
            {
                logger.LogWarning("{initiatorId} tried to connect without an answer", initiatorId);
                throw new UserFaultException("No answer provided");
            }

            request.Complete(answer);

            return new(offer, answer);
        }

        logger.LogDebug("{initiatorId} started negotiation with {targetId}", initiatorId, targetId);

        try
        {
            await NotifyTargetPeerAsync(
                initiatorId,
                targetId,
                offer,
                cancellationToken
            );

            var answerFromPeer = await negotiationEntry.Value.WhenCompleted;

            return answerFromPeer;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("The pair {pair} negotiation timed out.", pair);
            throw new UserFaultException($"Negotiation hasn't been completed within the time boundary of {OfferTimeout}.");
        }
        finally
        {
            logger.LogDebug("Removing pair {pair} negotiation from store.", pair);
            negotiationEntry.Remove();
        }
    }

    async Task NotifyTargetPeerAsync(
        string initiatorId,
        string targetId,
        WebRtcOffer offer,
        CancellationToken cancellationToken)
    {
        try
        {
            await messagesWriter.WriteAsync(
                targetId: targetId,
                senderId: initiatorId,
                messageContent: new(Type: MessageType.ConnectionAttempt, Payload: JsonSerializer.SerializeToElement(value: offer)),
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
        var tcs = new TaskCompletionSource<RtcMatchParameter>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetCanceled());
        OngoingNegotiation result = new(tcs, offer);
        return result;
    }
}
