using System.Collections.Concurrent;
using System.Text.Json;
using WebPhone.Backend.Storage;
using WebPhone.Contract;

namespace WebPhone.Backend.Services;

public class WebRtcParametersStorage
    : ConcurrentDictionary<
        string,
        (WebRtcSessionParameter, TaskCompletionSource<RtcMatchParameter>)
    > { }

public class RtcMatchMaker(
    MessagesRepository messagesRepository,
    WebRtcParametersStorage currentOffers
)
{
    static readonly TimeSpan OfferTimeout = TimeSpan.FromSeconds(30);

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

        var couple =
            initiatorId.GetHashCode() > targetId.GetHashCode()
                ? initiatorId + targetId
                : targetId + initiatorId;

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

            return Task.FromResult(new RtcMatchParameter(offer, answer));
        }

        if (currentOffer is not null)
        {
            return Task.FromResult(new RtcMatchParameter(currentOffer, null));
        }

        var tcs = new TaskCompletionSource<RtcMatchParameter>();

        currentOffers[couple] = (offer!, tcs);

        _ = Task.Run(
            async () =>
            {
                await messagesRepository.WriteMessageAsync(
                    nameof(MessageType.ConnectionAttempt),
                    JsonSerializer.SerializeToElement(offer),
                    initiatorId,
                    targetId,
                    cancellationToken
                );
                await Task.Delay(OfferTimeout);

                using var __ = cancellationToken.Register(() =>
                {
                    currentOffers.TryRemove(couple, out _);
                    tcs.TrySetCanceled();
                });

                if (
                    currentOffers.TryRemove(couple, out var offerAndTcs)
                    && offerAndTcs.Item2 == tcs
                )
                {
                    tcs.TrySetResult(new(null, null));
                }
            },
            cancellationToken
        );

        return tcs.Task;
    }
}
