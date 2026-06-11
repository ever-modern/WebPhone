using System.Collections.Concurrent;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using EverModern.Threading;
using Microsoft.Extensions.Logging;
using WebPhone.Domain;

namespace WebPhone.Services;

public class PeerConnector(
    IRtcConnector rtcConnector,
    ILogger<PeerConnector> logger,
    IBackendClient backendClient
) : IDisposable
{
    readonly ConcurrentDictionary<string, ConnectionEstablishmentProcess> _connectionProcesses =
        new();
    readonly EventSource _connectionEventSource = new();
    readonly KeyLocker<string> _perPeerLocker = new();

    bool _disposed = false;

    public INotifier StateChanged => _connectionEventSource;

    public IReadOnlyDictionary<string, Task<IRtcConnection?>> CurrentConnectionProcesses =>
        _connectionProcesses
            .Where(c => c.Value is { IsCompleted: false } or { ConnectedSuccessfully: true })
            .ToDictionary(c => c.Key, c => c.Value.WaitAsync(default));

    public IRtcConnection? FindReadyConnection(string peerId)
    {
        if (_connectionProcesses.TryGetValue(peerId, out var connectionProcess))
        {
            if (connectionProcess.ConnectedSuccessfully)
            {
                return connectionProcess.Result;
            }
        }
        return null;
    }

    public async Task<IRtcConnection?> ConnectToPeerAsync(
        string peerId,
        CancellationToken cancellationToken = default,
        WebRtcOffer? offer = null
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PeerConnector));
        logger.LogInformation(
            "[RTC][PeerConnector] ConnectToPeerAsync start. PeerId={PeerId}, IncomingOffer={IncomingOffer}, CancellationRequested={CancellationRequested}",
            peerId,
            offer is not null,
            cancellationToken.IsCancellationRequested
        );

        ConnectionEstablishmentProcess process;
        bool isNew = false;

        using (var locker = await _perPeerLocker.LockAsync(peerId, cancellationToken))
            if (_connectionProcesses.TryGetValue(peerId, out var existing))
            {
                if (offer is not null && existing is { IsOutgoing: true, IsCompleted: false })
                {
                    logger.LogInformation(
                        "[RTC][PeerConnector] Incoming offer preempts pending outgoing process. PeerId={PeerId}",
                        peerId
                    );
                    existing.Cancel();

                    isNew = true;
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var connectionTask = StartConnectingAsync(peerId, cts.Token, offer);

                    process = new(connectionTask, cts, false);
                    _connectionProcesses[peerId] = process;
                    _connectionEventSource.Invoke();
                }
                else
                {
                    logger.LogInformation(
                        "[RTC][PeerConnector] Reusing existing process. PeerId={PeerId}, IsOutgoing={IsOutgoing}, IsCompleted={IsCompleted}",
                        peerId,
                        existing.IsOutgoing,
                        existing.IsCompleted
                    );
                    process = existing;
                }
            }
            else
            {
                isNew = true;
                var isOutgoing = offer is null;
                logger.LogInformation(
                    "[RTC][PeerConnector] Creating new process. PeerId={PeerId}, IsOutgoing={IsOutgoing}",
                    peerId,
                    isOutgoing
                );

                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var connectionTask = StartConnectingAsync(peerId, cts.Token, offer);

                process = new(connectionTask, cts, isOutgoing);

                _connectionProcesses[peerId] = process;

                _connectionEventSource.Invoke();
            }

        IRtcConnection? connection;

        try
        {
            connection = await process.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "[RTC][PeerConnector] ConnectToPeerAsync canceled by caller token. PeerId={PeerId}",
                peerId
            );
            throw;
        }
        catch (Exception ex) when (ex is not RecursionDepthException and not HttpRequestException)
        {
            logger.LogWarning(
                ex,
                "[RTC][PeerConnector] ConnectToPeerAsync wait failed. PeerId={PeerId}, CancellationRequested={CancellationRequested}",
                peerId,
                cancellationToken.IsCancellationRequested
            );
            connection = null;
        }

        logger.LogInformation(
            "[RTC][PeerConnector] ConnectToPeerAsync completed. PeerId={PeerId}, Success={Success}, IsNew={IsNew}",
            peerId,
            connection is not null,
            isNew
        );

        if (isNew)
        {
            if (process.ConnectedSuccessfully is false)
            {
                logger.LogInformation(
                    "[RTC][PeerConnector] Cleaning failed process. Waiting peer lock. PeerId={PeerId}",
                    peerId
                );

                using var _ = await _perPeerLocker.LockAsync(peerId, cancellationToken);

                logger.LogInformation(
                    "[RTC][PeerConnector] Cleaning failed process. Acquired peer lock. PeerId={PeerId}",
                    peerId
                );

                if (
                    _connectionProcesses.TryGetValue(peerId, out var current)
                    && ReferenceEquals(current, process)
                )
                {
                    _connectionProcesses.TryRemove(peerId, out var __);
                    logger.LogInformation(
                        "[RTC][PeerConnector] Removed failed process from map. PeerId={PeerId}",
                        peerId
                    );
                }
            }

            _connectionEventSource.Invoke();

            connection?.StateChanged.Subscribe(async (newState, sub) =>
                {
                    using var _ = await _perPeerLocker.LockAsync(peerId);
                    if (newState == "closed")
                    {
                        if (_connectionProcesses.TryGetValue(peerId, out var current) && ReferenceEquals(current, process))
                        {
                            _connectionProcesses.TryRemove(peerId, out var __);
                        }

                        sub.Dispose();
                    }
                    _connectionEventSource.Invoke();
                }
            );
        }

        return connection;
    }

    public async Task<bool> ClosePeerConnectionAsync(
        string peerId,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PeerConnector));
        using var _ = await _perPeerLocker.LockAsync(peerId, cancellationToken);
        
        if (!_connectionProcesses.TryRemove(peerId, out var connectionProcess))            
            return false;
        
        connectionProcess.Cancel();
        if (connectionProcess.ConnectedSuccessfully)
        {
            await connectionProcess.Result!.DisposeAsync();
        }
        _connectionEventSource.Invoke();
        return true;
    }

    async Task<IRtcConnection?> StartConnectingAsync(
        string peerId,
        CancellationToken cancellationToken,
        WebRtcOffer? incomingOffer = null,
        int iteration = 0
    )
    {
        logger.LogInformation(
            "[RTC][PeerConnector] StartConnectingAsync. PeerId={PeerId}, IncomingOffer={IncomingOffer}, Iteration={Iteration}, CancellationRequested={CancellationRequested}",
            peerId,
            incomingOffer is not null,
            iteration,
            cancellationToken.IsCancellationRequested
        );
        RecursionDepthException.ThrowIfExceeded(iteration);
        WebRtcOffer? counterOffer = null;
        IRtcConnection? rtcConnection;
        if (incomingOffer is null)
        {
            rtcConnection = await rtcConnector.InitiateConnectionAsync(
                async (offer) =>
                {
                    var (responseOffer, responseAnswer) = await backendClient.ConnectRtcAsync(
                        new RtcConnectionRequest(peerId, offer, null),
                        cancellationToken
                    );
                    logger.LogInformation(
                        "[RTC][PeerConnector] Initiator backend response. PeerId={PeerId}, ResponseOffer={ResponseOffer}, ResponseAnswer={ResponseAnswer}",
                        peerId,
                        responseOffer is not null,
                        responseAnswer is not null
                    );
                    if (responseAnswer is null)
                    {
                        counterOffer = responseOffer;
                        return null;
                    }
                    else
                    {
                        return responseAnswer;
                    }
                },
                cancellationToken
            );

            if (counterOffer is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rtcConnection = await StartConnectingAsync(
                    peerId,
                    cancellationToken,
                    counterOffer,
                    iteration + 1
                );
            }

            return rtcConnection;
        }

        rtcConnection = await rtcConnector.AcceptConnectionAsync(
            incomingOffer,
            async answer =>
            {
                var (responseOffer, responseAnswer) = await backendClient.ConnectRtcAsync(
                    new RtcConnectionRequest(peerId, incomingOffer, answer),
                    cancellationToken
                );
                logger.LogInformation(
                    "[RTC][PeerConnector] Acceptor backend response. PeerId={PeerId}, ResponseOffer={ResponseOffer}, ResponseAnswer={ResponseAnswer}",
                    peerId,
                    responseOffer is not null,
                    responseAnswer is not null
                );

                if (responseAnswer is not null)
                {
                    return true;
                }

                counterOffer = responseOffer;

                return false;
            },
            cancellationToken
        );

        if (counterOffer is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rtcConnection = await StartConnectingAsync(
                peerId,
                cancellationToken,
                counterOffer,
                iteration + 1
            );
        }

        return rtcConnection;
    }

    public void Dispose()
    {
        _disposed = true;

        foreach (var connection in _connectionProcesses.Values)
        {
            connection.Cancel();
            if (connection.ConnectedSuccessfully)
                connection.Result!.Dispose();
        }
    }

    class RecursionDepthException : Exception
    {
        public static void ThrowIfExceeded(int iteration)
        {
            const int MaxRecursionDepth = 50;
            if (iteration > MaxRecursionDepth)
            {
                throw new RecursionDepthException();
            }
        }

        public RecursionDepthException()
            : base("Maximum recursion depth exceeded.") {}
    }
}
