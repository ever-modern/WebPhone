using System.Collections.Concurrent;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using EverModern.Threading.Queues;
using Microsoft.Extensions.Logging;
using WebPhone.Contract;

namespace WebPhone.Services;

class EntityLocker<TId>
    where TId : notnull
{
    readonly ConcurrentDictionary<TId, SemaphoreSlim> _locks = new();

    public async Task<AsyncScopeLocker> LockAsync(
        TId id,
        CancellationToken cancellationToken = default
    )
    {
        var locker = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        var scopeLocker = await locker.LockScopeAsync(cancellationToken);
        return scopeLocker;
    }
}

public sealed class PeerConnector(
    IRtcConnector webRtcConnector,
    ILogger<PeerConnector> logger,
    IBackendClient backendClient
) : IDisposable
{
    readonly ConcurrentDictionary<string, AccountedConnection> _connections = new();
    readonly EventSource _connectionEventSource = new();
    readonly EntityLocker<string> _entityLocker = new();

    public INotifier StateChanged => _connectionEventSource;

    public IReadOnlyDictionary<string, IRtcConnection> CurrentConnections =>
        _connections
            .Select(kvp =>
                (
                    kvp.Key,
                    kvp.Value.ConnectionTask.IsCompletedSuccessfully && kvp.Value.ConnectionTask.Result is not null
                        ? kvp.Value.ConnectionTask.Result
                        : null!
                )
            )
            .Where(kvp => kvp.Item2 is not null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Item2!);

    public async Task<IRtcConnection> GetPeerConnectionAsync(
        string peerId,
        CancellationToken cancellationToken = default
    )
    {
        AccountedConnection connection;

        using (var _ = await _entityLocker.LockAsync(peerId, cancellationToken))
            if (_connections.TryGetValue(peerId, out connection!))
            {
                logger.LogDebug(
                    "[PAIR] Returning existing connection slot for peer {PeerId}",
                    peerId
                );
            }
            else
            {
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var task = CreateOutgoingConnectionAsync(peerId, linkedCts.Token);
                connection = new AccountedConnection(peerId, true, linkedCts, task);
                _connections[peerId] = connection;
                _connectionEventSource.Invoke();
                logger.LogInformation(
                    "[PAIR] Created outgoing connection slot for peer {PeerId}",
                    peerId
                );
            }

        try
        {
            var result = await connection.ConnectionTask.WaitAsync(cancellationToken);


            return result
                ?? throw new InvalidOperationException($"Could not connect with peer {peerId}.");
        }
        catch
        {
            await RemoveConnectionIfMatchesAsync(peerId);
            throw;
        }
    }

    public async Task ClosePeerConnectionAsync(
        string peerId,
        CancellationToken cancellationToken = default
    )
    {
        if (!_connections.TryRemove(peerId, out var existing))
            return;

        existing.Cancellation.Cancel();
        await DisposeConnectionAgentIfReadyAsync(existing);
        _connectionEventSource.Invoke();
    }

    public async Task<IRtcConnection?> HandleIncomingConnectionRequestAsync(
        string peerId,
        WebRtcOffer offer,
        CancellationToken cancellationToken = default
    ) => await AcceptIncomingConnectionAsync(peerId, offer, cancellationToken);

    public bool IsConnectedTo(string peerId) => _connections.ContainsKey(peerId);

    async Task<IRtcConnection?> CreateOutgoingConnectionAsync(
        string peerId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            WebRtcOffer? counterOffer = null;
            var connection = await webRtcConnector.InitiateConnectionAsync(
                getAnswer: async offer =>
                {
                    logger.LogInformation(
                        "[RTC] Initiating offer exchange with peer {PeerId}. OfferType={OfferType}, HasSdp={HasSdp}",
                        peerId,
                        offer.Type,
                        !string.IsNullOrWhiteSpace(offer.Sdp)
                    );

                    var (responseOffer, answer) = await backendClient.ConnectRtcAsync(
                        new(peerId, offer, null),
                        cancellationToken
                    );

                    logger.LogInformation(
                        "[RTC] Offer exchange response from peer {PeerId}. ReturnedOffer={ReturnedOffer}, ReturnedAnswer={ReturnedAnswer}",
                        peerId,
                        responseOffer is not null,
                        answer is not null
                    );

                    if (answer is not null)
                    {
                        if (
                            string.IsNullOrWhiteSpace(answer.Type)
                            || string.IsNullOrWhiteSpace(answer.Sdp)
                        )
                        {
                            logger.LogWarning(
                                "[RTC] Invalid answer payload from peer {PeerId}. Type/Sdp required.",
                                peerId
                            );
                            return null;
                        }

                        logger.LogInformation(
                            "[RTC] Received answer from peer {PeerId}. AnswerType={AnswerType}, HasSdp={HasSdp}",
                            peerId,
                            answer.Type,
                            !string.IsNullOrWhiteSpace(answer.Sdp)
                        );

                        return answer;
                    }

                    if (responseOffer is not null)
                    {
                        counterOffer = responseOffer;
                        logger.LogInformation(
                            "[RTC] Peer {PeerId} already has active offer. Switching to accept counter-offer path.",
                            peerId
                        );
                        return null;
                    }
                    else if (answer is null)
                    {
                        logger.LogWarning(
                            "[RTC] No answer from peer {PeerId};",
                            peerId
                        );
                        return null;
                    }

                    logger.LogWarning(
                        "[RTC] Unexpected rtc-connect response from peer {PeerId}: both offer and answer are missing.",
                        peerId
                    );
                    return null;
                },
                cancellationToken: cancellationToken
            );

            if (connection is null && counterOffer is not null)
            {
                connection = await webRtcConnector.AcceptConnectionAsync(
                    counterOffer,
                    async answer =>
                    {
                        logger.LogInformation(
                            "[RTC] Sending answer for counter-offer to peer {PeerId}. AnswerType={AnswerType}, HasSdp={HasSdp}",
                            peerId,
                            answer.Type,
                            !string.IsNullOrWhiteSpace(answer.Sdp)
                        );

                        await backendClient.ConnectRtcAsync(
                            new(peerId, counterOffer, answer),
                            cancellationToken
                        );
                    },
                    cancellationToken
                );
            }

            if (connection is not null)
            {
                _connectionEventSource.Invoke();
            }

            return connection;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Outgoing connection request for peer {peerId} was superseded or turned down."
            );
        }
    }

    async Task<IRtcConnection?> AcceptIncomingConnectionAsync(
        string peerId,
        WebRtcOffer offer,
        CancellationToken cancellationToken
    )
    {
        using var _ = await _entityLocker.LockAsync(peerId, cancellationToken);
        if (_connections.TryGetValue(peerId, out var existing))
        {
            return await existing.ConnectionTask;
        }

        logger.LogInformation(
            "[RTC] Accepting incoming connection request from peer {PeerId}. OfferType={OfferType}, HasSdp={HasSdp}",
            peerId,
            offer.Type,
            !string.IsNullOrWhiteSpace(offer.Sdp)
        );

        var connection = await webRtcConnector.AcceptConnectionAsync(
            offer,
            async (answer) =>
            {
                logger.LogInformation(
                    "[RTC] Sending answer to peer {PeerId}. AnswerType={AnswerType}, HasSdp={HasSdp}",
                    peerId,
                    answer.Type,
                    !string.IsNullOrWhiteSpace(answer.Sdp)
                );

                await backendClient.ConnectRtcAsync(
                    new(peerId, offer, answer),
                    cancellationToken
                );
            },
            cancellationToken
        );

        if (connection is not null)
        {
            _connectionEventSource.Invoke();
        }

        return connection;
    }

    async Task RemoveConnectionIfMatchesAsync(string peerId)
    {
        AccountedConnection? removed = null;

        using var _ = await _entityLocker.LockAsync(peerId, default);

        if (_connections.TryGetValue(peerId, out var existing))
        {
            _connections.TryRemove(peerId, out removed);
        }

        if (removed is null)
            return;

        removed.Cancellation.Cancel();
        await DisposeConnectionAgentIfReadyAsync(removed);
        _connectionEventSource.Invoke();
    }

    static async Task DisposeConnectionAgentIfReadyAsync(AccountedConnection connection)
    {
        try
        {
            if (!connection.ConnectionTask.IsCompletedSuccessfully)
                return;

            var connectingResult = connection.ConnectionTask.Result;
            if (connectingResult is not null)
                await connectingResult.DisposeAsync();
        }
        catch { }
        finally
        {
            connection.Cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var (_, connection) in _connections)
        {
            connection.Cancellation.Cancel();
            _ = DisposeConnectionAgentIfReadyAsync(connection);
        }

        _connections.Clear();
    }

    record AccountedConnection(
        string PeerId,
        bool IsOutgoing,
        CancellationTokenSource Cancellation,
        Task<IRtcConnection?> ConnectionTask
    );
}
