using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using WebPhone.Backend.Storage;
using WebPhone.Domain;
using WebPhone.Domain.Communication;

namespace WebPhone.Backend.Services;

public class RtcMatchMaker(
    MessagesWriter messagesWriter,
    WebRtcParametersStorage currentOffers,
    ILogger<RtcMatchMaker> logger,
    PairMatchLocker locker,
    IHubContext<SignallingHub> hubContext
)
{
    static readonly TimeSpan OfferTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan PairLockTimeout = TimeSpan.FromSeconds(1);

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

        var pairLock = await locker.TryLockPairAsync(pair, PairLockTimeout, cancellationToken);

        if (pairLock is null)
        {
            logger.LogWarning(
                "[RTC] Pair lock timeout for pair {Pair}. Returning empty match result.",
                pair
            );
            return new(null, null);
        }

        using (pairLock)
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

            await messagesWriter.EnqueueAsync(
                MessageTypeJsonConverter.ToWireValue(MessageType.ConnectionAttempt),
                JsonSerializer.SerializeToElement(offer),
                initiatorId,
                targetId,
                cancellationToken
            );

            // Push the ConnectionAttempt message via SignalR so the target peer's
            // BackendMessagesChannel receives it in real time (the DB write alone
            // is picked up only by polling, which is no longer used).
            try
            {
                var exchangeResponse = new ExchangeResponse([
                    new MessageResponse(
                        CommonIdsGenerator.NewId(),
                        initiatorId,
                        MessageTypeJsonConverter.ToWireValue(MessageType.ConnectionAttempt),
                        DateTime.UtcNow,
                        JsonSerializer.SerializeToElement(offer)
                    )
                ]);

                await hubContext.Clients.User(targetId).SendAsync(
                    MessageSpecifications.Push.Key,
                    exchangeResponse,
                    cancellationToken
                );

                logger.LogInformation(
                    "[RTC] Pushed ConnectionAttempt to {TargetId} via SignalR",
                    targetId
                );
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "[RTC] Failed to push ConnectionAttempt to {TargetId} via SignalR (peer may not be connected to hub). Falling back to DB storage.",
                    targetId
                );
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