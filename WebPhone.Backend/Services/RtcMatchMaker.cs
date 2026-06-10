using System.Reflection.Metadata;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebPhone.Backend.Storage;
using WebPhone.Domain;
using WebPhone.Services;

namespace WebPhone.Backend.Services;



public class RtcMatchMaker(
    MessagesRepository messagesRepository,
    WebRtcParametersStorage currentOffers,
    ILogger<RtcMatchMaker> logger,
    PairMatchLocker locker
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

        if (offer is null && answer is null)
            throw new UserFaultException("Both offer and answer cannot be null.");

        var pair = new PeersPair(initiatorId, targetId);

        var tcs = new TaskCompletionSource<RtcMatchParameter>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        bool sendOffer = false;

        using (await locker.LockPairAsync(pair, cancellationToken))
        {
            var (currentOffer, waitingForAnswer) = currentOffers.GetValueOrDefault(pair);

            if (answer is not null)
            {
                if (offer is null)
                    throw new UserFaultException("Answer provided without corresponding offer.");

                if (currentOffer is null)
                    return new(null, null);

                if (!Equals(currentOffer, offer))
                    return new(currentOffer, null);

                currentOffers.TryRemove(pair, out _);

                waitingForAnswer.TrySetResult(new(currentOffer, answer));

                logger.LogInformation("[RTC] Answer matched for pair {Pair}", pair);

                return new(currentOffer, answer);
            }

            if (currentOffer is not null)
            {
                logger.LogInformation("[RTC] Existing offer found for pair {Pair}", pair);

                return new(currentOffer, null);
            }

            currentOffers[pair] = (offer!, tcs);

            sendOffer = true;

            logger.LogInformation("[RTC] Stored offer for pair {Pair}", pair);
        }

        if (sendOffer)
        {
            _ = RunOfferTimeoutAsync(pair, tcs);

            try
            {
                await messagesRepository.WriteMessageAsync(
                    MessageTypeJsonConverter.ToWireValue(MessageType.ConnectionAttempt),
                    JsonSerializer.SerializeToElement(offer),
                    initiatorId,
                    targetId,
                    cancellationToken
                );
            }
            catch
            {
                await RemoveOfferAsync(pair, tcs);

                throw;
            }
        }

        using var registration = cancellationToken.Register(() =>
        {
            tcs.TrySetCanceled(cancellationToken);
        });

        try
        {
            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            await RemoveOfferAsync(pair, tcs);

            throw;
        }
    }

    async Task RemoveOfferAsync(PeersPair pair, TaskCompletionSource<RtcMatchParameter> tcs)
    {
        using var _ = await locker.LockPairAsync(pair, CancellationToken.None);

        if (
            currentOffers.TryGetValue(pair, out var existing)
            && ReferenceEquals(existing.Item2, tcs)
        )
        {
            currentOffers.TryRemove(pair, out var __);
        }
    }

    async Task RunOfferTimeoutAsync(PeersPair pair, TaskCompletionSource<RtcMatchParameter> tcs)
    {
        try
        {
            await Task.Delay(OfferTimeout);

            using var _ = await locker.LockPairAsync(pair, CancellationToken.None);

            if (
                currentOffers.TryGetValue(pair, out var existing)
                && ReferenceEquals(existing.Item2, tcs)
            )
            {
                currentOffers.TryRemove(pair, out var __);

                logger.LogWarning("[RTC] Offer timed out for pair {Pair}", pair);

                tcs.TrySetResult(new(null, null));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[RTC] Error while processing offer timeout for pair {Pair}", pair);
        }
    }
}
