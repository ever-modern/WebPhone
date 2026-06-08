using System.Collections.Concurrent;
using WebPhone.Backend.Storage;
using WebPhone.Contract;

namespace WebPhone.Backend.Services;

public class RtcMatchMaker(MessagesRepository messagesRepository)
{
    static readonly TimeSpan OfferTimeout = TimeSpan.FromSeconds(30);
    readonly ConcurrentDictionary<
        string,
        (WebRtcSessionParameter, TaskCompletionSource<RtcMatchParameter>)
    > _currentOffers = [];

    public Task<RtcMatchParameter> MatchAsync(
        string initiatorId,
        string targetId,
        RtcMatchParameter parameters,
        CancellationToken cancellationToken
    )
    {
        var (offer, answer) = parameters;
        if (offer is not null && answer is not null || offer is null && answer is null)
        {
            throw new InvalidOperationException(
                "Match-making parameter must contain either offer or answer."
            );
        }

        var couple =
            initiatorId.GetHashCode() > targetId.GetHashCode()
                ? initiatorId + targetId
                : targetId + initiatorId;
        var (currentOffer, waitingForAnswer) = _currentOffers.GetValueOrDefault(couple);

        if (offer is not null)
        {
            if (currentOffer is not null)
            {
                return Task.FromResult(new RtcMatchParameter(currentOffer, null));
            }

            var tcs = new TaskCompletionSource<RtcMatchParameter>();

            _currentOffers[couple] = (offer, tcs);

            _ = Task.Run(
                async () =>
                {
                    await messagesRepository.WriteMessageAsync(
                        nameof(MessageType.ConnectionAttempt),
                        default,
                        initiatorId,
                        targetId,
                        cancellationToken
                    );
                    await Task.Delay(OfferTimeout);

                    using var __ = cancellationToken.Register(() =>
                    {
                        _currentOffers.TryRemove(couple, out _);
                        tcs.TrySetCanceled();
                    });

                    if (
                        _currentOffers.TryRemove(couple, out var offerAndTcs)
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

        currentOffer =
            currentOffer ?? throw new UserFaultException("No offer found for the answer.");

        _currentOffers.TryRemove(couple, out _);

        waitingForAnswer.TrySetResult(new RtcMatchParameter(currentOffer, parameters.Answer));

        return Task.FromResult(new RtcMatchParameter(offer, answer));
    }
}
