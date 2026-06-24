using System.Collections.Concurrent;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using Microsoft.Extensions.Logging;
using WebPhone.Domain;
using WebPhone.Services.Channels;

namespace WebPhone.Services;

public class MediaEnabler
{
    public async Task SetMediaAsync(MediaState mediaState) {}
}

public class PeerConnectionsDispatcher(
    IRtcConnector rtcConnector,
    ILoggerFactory loggerFactory,
    IBackendClient backendClient
) : IDisposable
{
    readonly ConcurrentDictionary<string, PeerConnector> _connectors = new();
    readonly ObservedValue<IReadOnlyDictionary<string, InteractionState>> _connectionEventSource = new(new Dictionary<string, InteractionState>());
    readonly CancellationTokenSource _cts = new();
    readonly Lock _disposeLock = new();

    bool _disposed = false;

    public IValueNotifier<IReadOnlyDictionary<string, InteractionState>> ConnectionsChange => _connectionEventSource;

    public Task<IRtcConnection> ConnectAsync(string peerId, CancellationToken cancellationToken = default, WebRtcOffer? offer = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var connector = _connectors.GetOrAdd(
            peerId,
            id =>
            {
                var newConnector = NewConnector(peerId);
                _ = newConnector.ConnectAsync(cancellationToken, offer);
                return newConnector;
            }
        );

        var connectionTask = connector.Connecting;

        return connectionTask!;
    }

    public Task ClosePeerConnectionAsync(string peerId, CancellationToken cancellationToken = default)
    {
        if (_connectors.TryGetValue(peerId, out var connector))
        {
            return Task.FromResult(connector.CloseConnection());
        }

        return Task.CompletedTask;
    }

    public INotifier StateChanged => _connectionEventSource;

    public IReadOnlyDictionary<string, Task<IRtcConnection>> CurrentConnectionProcesses =>
        _connectors
            .Where(c => c.Value.Connecting is { IsCompleted: false } or { IsCompletedSuccessfully: true })
            .ToDictionary(c => c.Key, c => c.Value.Connecting!);

    public IRtcConnection? FindReadyConnection(string peerId)
        => _connectors.TryGetValue(peerId, out var connector) ? connector.GetReadyConnection() : null;

    public void Dispose()
    {
        if (_disposeLock.TryEnter() is false)
            return;

        _disposed = true;
        _cts.Cancel();

        foreach (var (_, connector) in _connectors)
        {
            connector.CloseConnection();
        }
    }

    PeerConnector NewConnector(string peerId)
    {
        var logger = loggerFactory.CreateLogger($"[PeerConnector][User:{peerId}]");
        var connector = new PeerConnector(
            peerId,
            logger,
            backendClient,
            rtcConnector
        );

        connector.ConnectionChanged.Subscribe(newConnectionState =>
            {
                var newConnectionsState = _connectionEventSource.Value.ToDictionary();
                newConnectionsState[peerId] = newConnectionState;
                _connectionEventSource.Change(newConnectionsState);
            }
        );

        return connector;
    }
}
