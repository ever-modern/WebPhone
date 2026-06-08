using System.Collections.Concurrent;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using EverModern.Threading.Queues;
using Microsoft.Extensions.Logging;
using WebPhone.Contract;

namespace WebPhone.Services;

public sealed class PeerConnector(
    IRtcConnector webRtcConnector,
    ILogger<PeerConnector> logger,
    BackendClient backendClient
) : IDisposable
{
    readonly SemaphoreSlim _locker = new(1, 1);
    readonly ConcurrentDictionary<string, AccountedConnection> _connections = new();
    readonly EventSource _connectionEventSource = new();

    static RtcConnectionRequest CreateRequest(
        string peerId,
        WebRtcOffer offer,
        WebRtcAnswer? answer = null
    ) =>
        new(
            peerId,
            new(offer.Sdp, offer.Type),
            answer is null ? null : new(answer.Sdp, answer.Type)
        );

    public INotifier StateChanged => _connectionEventSource;

    public IReadOnlyDictionary<string, RtcConnection> CurrentConnections =>
        _connections
            .Select(kvp =>
                (
                    kvp.Key,
                    kvp.Value.ConnectionTask.IsCompletedSuccessfully
                        ? kvp.Value.ConnectionTask.Result
                        : null!
                )
            )
            .Where(kvp => kvp.Item2 is not null)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Item2!);

    public async Task<RtcConnection> GetPeerConnectionAsync(
        string peerId,
        CancellationToken cancellationToken = default
    )
    {
        AccountedConnection connection;

        using (var _ = await _locker.LockScopeAsync(cancellationToken))
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

    public async Task HandleIncomingConnectionRequestAsync(
        string peerId,
        WebRtcOffer offer,
        CancellationToken cancellationToken = default
    ) => await AcceptIncomingConnectionAsync(peerId, offer, cancellationToken);

    async Task<RtcConnection?> CreateOutgoingConnectionAsync(
        string peerId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            WebRtcOffer? counterOffer = null;
            var connection = await webRtcConnector.InitiateConnectionAsync(
                async offer =>
                {
                    var (responseOffer, answer) = await backendClient.ConnectRtcAsync(
                        CreateRequest(peerId, offer, null),
                        cancellationToken
                    );

                    if (responseOffer is not null)
                    {
                        counterOffer = new(responseOffer.Sdp, responseOffer.Type);
                        return null;
                    }
                    else if (answer is null)
                    {
                        return null;
                    }

                    return new(answer.Sdp, answer.Sdp);
                },
                cancellationToken
            );

            if (connection is null && counterOffer is not null)
            {
                connection = await webRtcConnector.AcceptConnectionAsync(
                    counterOffer,
                    async answer =>
                        await backendClient.ConnectRtcAsync(
                            CreateRequest(peerId, counterOffer, answer),
                            cancellationToken
                        ),
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

    async Task<RtcConnection?> AcceptIncomingConnectionAsync(
        string peerId,
        WebRtcOffer offer,
        CancellationToken cancellationToken
    )
    {
        var connection = await webRtcConnector.AcceptConnectionAsync(
            offer,
            async (answer) =>
                await backendClient.ConnectRtcAsync(
                    CreateRequest(peerId, offer, answer),
                    cancellationToken
                ),
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

        using (var _ = await _locker.LockScopeAsync())
            try
            {
                if (_connections.TryGetValue(peerId, out var existing))
                {
                    _connections.TryRemove(peerId, out removed);
                }
            }
            finally
            {
                _locker.Release();
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
        Task<RtcConnection?> ConnectionTask
    );
}
