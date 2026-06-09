using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebPhone.Backend.Storage;
using WebPhone.Contract;

namespace WebPhone.Backend.Services;

public class WebRtcParametersStorage
    : ConcurrentDictionary<string, (WebRtcOffer, TaskCompletionSource<RtcMatchParameter>)> { }

public class RtcMatchMaker(
    MessagesRepository messagesRepository,
    WebRtcParametersStorage currentOffers,
    ILogger<RtcMatchMaker> logger
)
{
    static readonly TimeSpan OfferTimeout = TimeSpan.FromSeconds(30);

    static string ComposeCoupleId(string id1, string id2) =>
        (string.CompareOrdinal(id1, id2) > 0 ? id1 + id2 : id2 + id1)
        + $"{id1.Length}-{id2.Length}";

    public Task<RtcMatchParameter> MatchAsync(
        string initiatorId,
        string targetId,
        RtcMatchParameter parameters,
        CancellationToken cancellationToken
    )
    {
        var (offer, answer) = parameters;

        if (offer is null && answer is null)
        {
            throw new UserFaultException($"Both offer and answer cannot be null.");
        }

        logger.LogInformation(
            "[RTC] Match request {InitiatorId}->{TargetId}. OfferPresent={OfferPresent}, AnswerPresent={AnswerPresent}",
            initiatorId,
            targetId,
            offer is not null,
            answer is not null
        );

        var couple = ComposeCoupleId(initiatorId, targetId);

        var (currentOffer, waitingForAnswer) = currentOffers.GetValueOrDefault(couple);

        if (answer is not null)
        {
            if (offer is null)
            {
                throw new UserFaultException($"Answer provided without the offer it's meant for.");
            }

            if (currentOffer != offer)
            {
                throw new UserFaultException("Offer mismatch.");
            }

            currentOffer =
                currentOffer ?? throw new UserFaultException("No offer found for the answer.");

            currentOffers.TryRemove(couple, out _);

            waitingForAnswer.TrySetResult(new RtcMatchParameter(currentOffer, parameters.Answer));
            logger.LogInformation(
                "[RTC] Answer matched for pair {Couple}. Completing waiting initiator task.",
                couple
            );

            return Task.FromResult(new RtcMatchParameter(offer, answer));
        }

        if (currentOffer is not null)
        {
            logger.LogInformation(
                "[RTC] Existing offer found for pair {Couple}. Returning current offer to requester.",
                couple
            );
            return Task.FromResult(new RtcMatchParameter(currentOffer, null));
        }

        var tcs = new TaskCompletionSource<RtcMatchParameter>();

        currentOffers[couple] = (offer!, tcs);
        logger.LogInformation(
            "[RTC] Stored offer for pair {Couple}; waiting for answer up to {TimeoutSeconds}s.",
            couple,
            OfferTimeout.TotalSeconds
        );

        _ = Task.Run(
            async () =>
            {
                await messagesRepository.WriteMessageAsync(
                    MessageTypeJsonConverter.ToWireValue(MessageType.ConnectionAttempt),
                    JsonSerializer.SerializeToElement(offer),
                    initiatorId,
                    targetId,
                    cancellationToken
                );

                using var __ = cancellationToken.Register(() =>
                {
                    currentOffers.TryRemove(couple, out _);
                    tcs.TrySetCanceled();
                });

                await Task.Delay(OfferTimeout);

                if (
                    currentOffers.TryRemove(couple, out var offerAndTcs)
                    && offerAndTcs.Item2 == tcs
                )
                {
                    logger.LogWarning(
                        "[RTC] Offer timed out for pair {Couple} after {TimeoutSeconds}s.",
                        couple,
                        OfferTimeout.TotalSeconds
                    );
                    tcs.TrySetResult(new(null, null));
                }
            },
            cancellationToken
        );

        return tcs.Task;
    }
}
