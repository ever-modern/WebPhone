using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using EverModern.Threading.Locks;
using Microsoft.Extensions.Logging;
using WebPhone.Domain;

namespace WebPhone;

public class PeerConnector(
    string peerId,
    ILogger logger,
    IBackendClient backendClient,
    IRtcConnector rtcConnector
)
{
    const int _maxCounterOfferAttempts = 3;
    readonly Lock _locker = new();
    readonly ObservedValue<InteractionState> _connectionEventSource = new(InteractionState.Disconnected.Instance);
    readonly CancellationTokenSource _cts = new();

    public IValueNotifier<InteractionState> ConnectionChanged => _connectionEventSource;

    public Task<IRtcConnection?> Connecting =>
        _connecting.WhenAny();

    readonly RtcConnectionProcesses _connecting = new();

    public IRtcConnection? GetReadyConnection()
        => _connecting.AnyReady();

    public Task<IRtcConnection> ConnectAsync(
        CancellationToken cancellationToken = default,
        WebRtcOffer? offer = null
    )
    {
        logger.LogDebug(
            "Required a peer connection {offer}.",
            offer is null ? "without an incoming offer" : "with an incoming offer"
        );

        var ready = _connecting.AnyReady();
        if (ready is not null)
        {
            logger.LogInformation("A connection is already established. Returning the ready result.");
            return Task.FromResult(ready);
        }

        using (var _ = _locker.LockScope())
        {
            logger.LogInformation("No ready connection yet.");

            _connectionEventSource.Change(InteractionState.Connecting.Instance);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _connecting.Add(
                StartConnectingAsync(cancellationToken, offer)
                    .ContinueWith(
                        t =>
                        {
                            if (t.IsFaulted)
                            {
                                logger.LogError(t.Exception, "Could not establish connection.");
                                _connectionEventSource.Change(InteractionState.Disconnected.Instance);
                                throw t.Exception;
                            }
                            _connectionEventSource.Change(InteractionState.Connected.Instance);
                            return WithSubscription(t.Result);
                        },
                        CancellationToken.None
                    ),
                cts,
                offer
            );
        }

        return _connecting.WhenAny();
    }

    public async ValueTask CloseAllConnections()
    {
        foreach (var connection in _connecting.DrainAll())
        {
            await rtcConnector.CloseConnectionAsync(connection);
        }
    }

    IRtcConnection WithSubscription(IRtcConnection connection)
    {
        connection.State.Subscribe((newState, sub) =>
            {
                if (newState is RtcConnectionState.Closed or RtcConnectionState.Failed or RtcConnectionState.Disconnected)
                {
                    using var _ = _locker.LockScope();
                    sub.Dispose();
                    rtcConnector.CloseConnectionAsync(connection);
                    _connectionEventSource.Change(_connecting.State);
                }
            }
        );

        return connection;
    }

    async Task<IRtcConnection> StartConnectingAsync(
        CancellationToken cancellationToken,
        WebRtcOffer? incomingOffer = null
    )
    {
        NegotiationResult negotiationResult;

        if (incomingOffer is null)
        {
            logger.LogDebug("Attempting to initiate an RTC connection.");
            negotiationResult = await InitiateConnectionAsync(cancellationToken);
        }
        else
        {
            logger.LogDebug("Answering an incoming offer.");
            negotiationResult = await AcceptOfferAsync(
                incomingOffer: incomingOffer,
                cancellationToken: cancellationToken
            );
        }

        var (connection, counterOffer) = negotiationResult;

        if (connection is not null)
        {
            return connection;
        }

        int attempts = 0;
        while (counterOffer is not null)
        {
            logger.LogInformation("Attempt to connect encountered a counter-offer.");
            if (attempts++ >= _maxCounterOfferAttempts)
            {
                throw new RtcConnectionException("Too many failed attempts to act on a counter offer.");
            }

            (connection, counterOffer) = await AcceptOfferAsync(counterOffer, cancellationToken);

            if (connection is not null)
            {
                return connection;
            }
        }

        throw new RtcConnectionException("Unable to connect.");
    }

    async Task<NegotiationResult> InitiateConnectionAsync(
        CancellationToken cancellationToken = default
    )
    {
        WebRtcOffer? counterOffer = null;
        var rtcConnection = await rtcConnector.InitiateConnectionAsync(
            async (offer) =>
            {
                logger.LogInformation("Sending offer to peer {peerId}", peerId);

                var rtcMatchResponse = await backendClient
                    .ConnectRtcAsync(
                        new RtcConnectionRequest(peerId, offer, null),
                        cancellationToken
                    )
                    .ContinueWith(
                        t =>
                        {
                            if (t.Exception is null)
                                return t.Result;

                            logger.LogError($"Error sending connection request: {t.Exception}");
                            throw t.Exception;
                        },
                        cancellationToken
                    );

                var responseOffer = rtcMatchResponse.Offer;
                var responseAnswer = rtcMatchResponse.Answer;
                var connectionId = rtcMatchResponse.Id;

                logger.LogInformation(
                    "Initiator backend response. PeerId={PeerId}, ResponseOffer={ResponseOffer}, ResponseAnswer={ResponseAnswer}",
                    peerId,
                    responseOffer is not null,
                    responseAnswer is not null
                );

                if (responseAnswer is null)
                {
                    if (responseOffer is null)
                        logger.LogWarning("Server returned neither a response, nor a counter offer.");
                    else
                        logger.LogInformation(
                            "Received counter offer from server. PeerId={PeerId}",
                            peerId
                        );

                    counterOffer = responseOffer;
                    return (null, null);
                }

                return (responseAnswer, connectionId);
            },
            cancellationToken
        );

        if (rtcConnection is null)
        {
            return new(counterOffer);
        }

        return new(rtcConnection);
    }

    async Task<NegotiationResult> AcceptOfferAsync(
        WebRtcOffer incomingOffer,
        CancellationToken cancellationToken = default
    )
    {
        WebRtcOffer? counterOffer = null;
        var rtcConnection = await rtcConnector.AcceptConnectionAsync(
            incomingOffer,
            async answer =>
            {
                logger.LogInformation("Sending answer to peer {peerId}", peerId);

                var rtcMatchParameter = await backendClient
                    .ConnectRtcAsync(
                        new RtcConnectionRequest(peerId, incomingOffer, answer),
                        cancellationToken
                    )
                    .ContinueWith(
                        t =>
                        {
                            if (t.Exception is null)
                                return t.Result;

                            logger.LogError($"Error sending connection request with incoming offer = {incomingOffer}:\n {t.Exception}");
                            throw t.Exception;
                        },
                        cancellationToken
                    );

                var responseOffer = rtcMatchParameter.Offer;
                var responseAnswer = rtcMatchParameter.Answer;
                var connectionId = rtcMatchParameter.Id;

                logger.LogInformation(
                    "ResponseOffer={ResponseOffer}, ResponseAnswer={ResponseAnswer}",
                    responseOffer is not null,
                    responseAnswer is not null
                );

                if (responseAnswer is null)
                {
                    logger.LogDebug("Answer to offer is empty");

                    if (responseOffer is not null)
                    {
                        logger.LogDebug("Received counter offer");
                        counterOffer = responseOffer;
                    }
                    else
                    {
                        logger.LogDebug("Received no answer and no counter offer");
                    }

                    return connectionId;
                }

                logger.LogInformation("Successfully sent answer.");

                return connectionId;
            },
            cancellationToken
        );

        if (rtcConnection is null)
        {
            return new(default(WebRtcOffer));
        }

        return new(rtcConnection);
    }
}

record struct NegotiationResult(
    IRtcConnection? Connection,
    WebRtcOffer? CounterOffer
)
{
    public NegotiationResult(IRtcConnection connection)
        : this(connection, null) {}

    public NegotiationResult(WebRtcOffer offer)
        : this(null, offer) {}
}
