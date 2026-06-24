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
    readonly ObservedValue<InteractionState> _connectionEventSource = new(
        InteractionState.Disconnected.Instance
    );
    readonly CancellationTokenSource _cts = new();

    public IValueNotifier<InteractionState> ConnectionChanged => _connectionEventSource;

    public Task<IRtcConnection>? Connecting => _connecting;

    Task<IRtcConnection>? _connecting;

    public IRtcConnection? GetReadyConnection()
    {
        using var _ = _locker.LockScope();

        if (_connecting?.IsCompletedSuccessfully is not true)
            return null;

        return _connecting.Result;
    }

    public Task<IRtcConnection> ConnectAsync(
        CancellationToken cancellationToken = default,
        WebRtcOffer? offer = null
    )
    {
        Task<IRtcConnection> task;

        logger.LogDebug(
            "Required a peer connection {offer}.",
            offer is null ? "without an incoming offer" : "with an incoming offer"
        );

        using (var _ = _locker.LockScope())
        {
            if (_connecting is not null && !_connecting.IsFaulted)
                return _connecting;

            if (_connecting is null)
            {
                logger.LogInformation(
                    "No connection yet or the previous has been closed. Starting new negotiation."
                );
            }
            else if (_connecting.IsFaulted)
            {
                logger.LogInformation("Previous connection attempt failed. Starting new attempt.");
            }

            _connectionEventSource.Change(InteractionState.Connecting.Instance);
            _connecting = StartConnectingAsync(cancellationToken, offer)
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
                );
        }

        return _connecting;
    }

    public bool CloseConnection()
    {
        using var _ = _locker.LockScope();
        if (_connecting?.IsCompletedSuccessfully != true)
            return false;

        _connecting.Result.Dispose();
        return true;
    }

    IRtcConnection WithSubscription(IRtcConnection connection)
    {
        connection.StateChanged.Subscribe(
            (newState, sub) =>
            {
                if (newState is "closed")
                {
                    using var _ = _locker.LockScope();
                    _connecting = null;
                    sub.Dispose();
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
            logger.LogInformation("Connected successfully.");
            return connection;
        }

        int attempts = 0;
        while (counterOffer is not null)
        {
            logger.LogInformation("Attempt to connect encountered a counter-offer.");
            if (attempts++ >= _maxCounterOfferAttempts)
            {
                throw new RtcConnectionException(
                    "Too many failed attempts to act on a counter offer."
                );
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

                var (responseOffer, responseAnswer) = await backendClient
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

                logger.LogInformation(
                    "Initiator backend response. PeerId={PeerId}, ResponseOffer={ResponseOffer}, ResponseAnswer={ResponseAnswer}",
                    peerId,
                    responseOffer is not null,
                    responseAnswer is not null
                );

                if (responseAnswer is null)
                {
                    if (responseOffer is null)
                        logger.LogWarning(
                            "Server returned neither a response, nor a counter offer."
                        );
                    else
                        logger.LogInformation(
                            "Received counter offer from server. PeerId={PeerId}",
                            peerId
                        );

                    counterOffer = responseOffer;
                    return null;
                }

                return responseAnswer;
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

                            logger.LogError(
                                $"Error sending connection request with incoming offer = {incomingOffer}:\n {t.Exception}"
                            );
                            throw t.Exception;
                        },
                        cancellationToken
                    );

                var (responseOffer, responseAnswer) = rtcMatchParameter;

                logger.LogInformation(
                    "ResponseOffer={ResponseOffer}, ResponseAnswer={ResponseAnswer}",
                    responseOffer,
                    responseAnswer
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

                    return false;
                }

                logger.LogInformation("Successfully sent answer {answer}", answer);
                return true;
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

record struct NegotiationResult(IRtcConnection? Connection, WebRtcOffer? CounterOffer)
{
    public NegotiationResult(IRtcConnection connection)
        : this(connection, null) { }

    public NegotiationResult(WebRtcOffer offer)
        : this(null, offer) { }
}

public class RtcConnectionException(string message) : InvalidOperationException(message);
