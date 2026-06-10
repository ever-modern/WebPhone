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

        ConnectionEstablishmentProcess process;
        bool isNew = false;

        using (var locker = await _perPeerLocker.LockAsync(peerId, cancellationToken))
            if (_connectionProcesses.TryGetValue(peerId, out var existing))
            {
                if (offer is not null && existing.IsOutgoing && !existing.IsCompleted)
                {
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
                    process = existing;
                }
            }
            else
            {
                isNew = true;
                var isOutgoing = offer is null;

                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var connectionTask = StartConnectingAsync(peerId, cts.Token, offer);

                process = new(connectionTask, cts, isOutgoing);

                _connectionProcesses[peerId] = process;

                _connectionEventSource.Invoke();
            }

        IRtcConnection? connection;

        try
        {
            connection = await process.WaitAsync(default);
        }
        catch (Exception ex) when (ex is not RecursionDepthException and not HttpRequestException)
        {
            connection = null;
        }

        if (isNew)
        {
            if (process.ConnectedSuccessfully is false)
            {
                using var _ = await _perPeerLocker.LockAsync(peerId);
                if (
                    _connectionProcesses.TryGetValue(peerId, out var current)
                    && ReferenceEquals(current, process)
                )
                {
                    _connectionProcesses.TryRemove(peerId, out var __);
                }
            }

            _connectionEventSource.Invoke();

            connection?.StateChanged.Subscribe(
                async (newState, sub) =>
                {
                    using var _ = await _perPeerLocker.LockAsync(peerId);
                    if (newState == "closed")
                    {
                        if (
                            _connectionProcesses.TryGetValue(peerId, out var current)
                            && ReferenceEquals(current, process)
                        )
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
        if (_connectionProcesses.TryRemove(peerId, out var connectionProcess))
        {
            connectionProcess.Cancel();
            if (connectionProcess.ConnectedSuccessfully)
            {
                connectionProcess.Result!.Dispose();
            }
            _connectionEventSource.Invoke();
            return true;
        }
        return false;
    }

    async Task<IRtcConnection?> StartConnectingAsync(
        string peerId,
        CancellationToken cancellationToken,
        WebRtcOffer? incomingOffer = null,
        int iteration = 0
    )
    {
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
            : base("Maximum recursion depth exceeded.") { }
    }
}
