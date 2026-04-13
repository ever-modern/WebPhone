using System.Diagnostics.Contracts;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using WebPhone.Messages;
using WebPhone.Services.Channels;

namespace WebPhone.Services;

class ContactManager(PeerConnector peerConnector, string contactId) : IDisposable
{
    readonly EventSource _stateChanged = new();
    public INotifier StateChanged => _stateChanged;

    public (InteractionType Type, Action Cancel) _interaction;

    public InteractionType State => _interaction.Type;

    bool _isEnabled = false;

    event Action _onDispose = () => { };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_isEnabled == true)
            return;

        _onDispose += peerConnector
            .StateChanged.Subscribe(() =>
            {
                var existingConnection = peerConnector.CurrentConnections.GetValueOrDefault(
                    contactId
                );

                if (State is not InteractionType.None && existingConnection is null)
                {
                    SetState(InteractionType.None, () => { });
                }
                else if (State < InteractionType.Connected && existingConnection is not null)
                {
                    SetState(
                        InteractionType.Connected,
                        () => _ = peerConnector.ClosePeerConnectionAsync(contactId, default)
                    );
                }
            })
            .Dispose;

        _isEnabled = true;
    }

    public void Dispose()
    {
        if (_isEnabled == false)
            return;

        _onDispose();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is not InteractionType.None)
            return;

        var connectingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        SetState(InteractionType.Connecting, connectingCts.Cancel);

        var connection = await peerConnector
            .GetPeerConnectionAsync(contactId, connectingCts.Token)
            .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);

        if (connection is null)
        {
            SetState(InteractionType.None, () => { });
            return;
        }

        var disconnectCts = new CancellationTokenSource();

        SetState(InteractionType.Connected, disconnectCts.Cancel);

        var incomingCallListener = new CallMaintainer(connection, TimeSpan.FromMilliseconds(500));

        _ = incomingCallListener
            .WhenReceivedCallPingAsync(disconnectCts.Token)
            .ContinueWith(
                t =>
                {
                    if (t.IsCanceled || t.IsFaulted)
                        return;
                    SetState(InteractionType.ReceivingCall, () => { });
                },
                disconnectCts.Token
            );
    }

    public void StopConnecting()
    {
        if (_interaction.Type == InteractionType.Connecting)
            _interaction.Cancel();
    }

    public void Disconnect()
    {
        if (_interaction.Type == InteractionType.Connected)
            _interaction.Cancel();
    }

    public async Task StartCallAsync(CancellationToken cancellationToken = default)
    {
        if (_interaction.Type is not InteractionType.Connected)
            return;

        var currentConnections = peerConnector.CurrentConnections;

        var connection = currentConnections.GetValueOrDefault(contactId);
        if (connection is null)
        {
            SetState(InteractionType.None, () => { });
            return;
        }

        var callingCts = new CancellationTokenSource();

        SetState(InteractionType.Calling, callingCts.Cancel);

        var callMaintainer = new CallMaintainer(connection, TimeSpan.FromMilliseconds(500));

        _ = callMaintainer.MaintainCallAsync(callingCts.Token);
        var __ = callMaintainer
            .WhenReceivedCallPingAsync(callingCts.Token)
            .ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully == false)
                    return;

                var stopCallCts = new CancellationTokenSource();
                callingCts.Cancel();

                SetState(
                    InteractionType.Speaking,
                    () =>
                    {
                        stopCallCts.Cancel();
                        _ = DisableMediaAsync(connection);
                    }
                );

                _ = EnableMediaAsync(connection);
                _ = callMaintainer
                    .WhenCallStoppedAsync(stopCallCts.Token)
                    .ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            stopCallCts.Cancel();
                            _ = DisableMediaAsync(connection);
                        }
                    });
            });
    }

    public void AcceptCall()
    {
        var currentConnections = peerConnector.CurrentConnections;
        var connection = currentConnections.GetValueOrDefault(contactId);
        if (connection is null)
            return;

        var stopCallCts = new CancellationTokenSource();
        var callMaintainer = new CallMaintainer(connection, TimeSpan.FromMilliseconds(500));
        _ = callMaintainer.MaintainCallAsync(stopCallCts.Token);
        SetState(
            InteractionType.Speaking,
            () =>
            {
                stopCallCts.Cancel();
                _ = DisableMediaAsync(connection);
            }
        );
        _ = EnableMediaAsync(connection);
    }

    public void DeclineCall()
    {
        var currentConnections = peerConnector.CurrentConnections;
        var connection = currentConnections.GetValueOrDefault(contactId);
        if (connection is null)
            return;

        using var channel = new RtcConnectionMessageChannel(connection);
        _ = channel.Writer.WriteAsync(new RtcMessage(RtcMessageType.RejectCall));

        SetState(
            InteractionType.None,
            () => _ = peerConnector.ClosePeerConnectionAsync(contactId, default)
        );
    }

    public void EndCall()
    {
        if (_interaction.Type is not InteractionType.Speaking)
            return;

        _interaction.Cancel();
    }

    public void SendMessage(string text)
    {
        var currentConnections = peerConnector.CurrentConnections;
        var connection = currentConnections.GetValueOrDefault(contactId);
        if (connection is null)
            return;

        using var chat = new RtcConnectionMessageChannel(connection);

        _ = chat.Writer.WriteAsync(new RtcMessage(RtcMessageType.User, text));
    }

    void SetState(InteractionType newState, Action cancelInteraction)
    {
        _interaction = (newState, cancelInteraction);
        _stateChanged.Invoke();
    }

    static async Task EnableMediaAsync(RtcConnection connection)
    {
        await connection.EnableAudioInputAsync();
        await connection.EnableAudioOutputAsync();

        //await connection.EnableVideoInputAsync();
        //await connection.EnableVideoOutputAsync();
    }

    static async Task DisableMediaAsync(RtcConnection connection)
    {
        await connection.DisableAudioInputAsync();
        await connection.DisableVideoInputAsync();
        await connection.DisableAudioOutputAsync();
        await connection.DisableVideoOutputAsync();
    }
}
