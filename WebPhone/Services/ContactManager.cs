using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using WebPhone.Messages;
using WebPhone.Services.Channels;

namespace WebPhone.Services;

class ContactManager(PeerConnector peerConnector, string contactId, VideoCallState videoCallState)
    : IDisposable
{
    readonly EventSource _stateChanged = new();
    public INotifier StateChanged => _stateChanged;

    sealed class Interaction(InteractionType type, Action cancel)
    {
        public InteractionType Type { get; } = type;
        readonly Action _cancel = cancel;

        public void Cancel() => _cancel();
    }

    Interaction _interaction = new(InteractionType.None, () => { });

    public InteractionType State => _interaction.Type;

    bool _isEnabled = false;
    bool _useVideo = false;
    bool _incomingIsVideoCall = false;
    CancellationTokenSource? _sessionCts;
    CancellationTokenSource? _syncCts;
    Task? _syncTask;
    Subscription? _connectionStateSubscription;

    event Action OnDispose = () => { };

    static readonly TimeSpan _criticalSignalTime = TimeSpan.FromSeconds(4);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_isEnabled == true)
            return;

        _isEnabled = true;
        StartSyncLoop();

        OnDispose += peerConnector
            .StateChanged.Subscribe(() =>
            {
                var existingConnection = GetConnection();

                if (
                    State
                        is InteractionType.Connected
                            or InteractionType.Calling
                            or InteractionType.ReceivingCall
                            or InteractionType.Speaking
                    && existingConnection is null
                )
                {
                    StopSession();
                    SetState(InteractionType.None, () => { });
                }
                else if (State < InteractionType.Connected && existingConnection is not null)
                {
                    StartSession(existingConnection);
                    SetState(InteractionType.Connected, () => { });
                }
            })
            .Dispose;

        var existing = GetConnection();
        if (existing is not null)
        {
            StartSession(existing);
            SetState(InteractionType.Connected, () => { });
        }

        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_isEnabled == false)
            return;

        _isEnabled = false;
        StopSession();
        _syncCts?.Cancel();
        _syncCts?.Dispose();
        _syncCts = null;
        OnDispose();
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

        if (State is not InteractionType.Connecting)
            return;

        StartSession(connection);
        SetState(InteractionType.Connected, () => { });
    }

    public void StopConnecting()
    {
        if (_interaction.Type == InteractionType.Connecting)
            SetState(InteractionType.None, () => { });
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        StopSession();
        SetState(InteractionType.None, () => { });
        await peerConnector.ClosePeerConnectionAsync(contactId, cancellationToken);
    }

    public async Task StartCallAsync(CancellationToken cancellationToken = default)
    {
        _useVideo = false;
        await StartCallCoreAsync(cancellationToken);
    }

    public async Task StartVideoCallAsync(CancellationToken cancellationToken = default)
    {
        _useVideo = true;
        await StartCallCoreAsync(cancellationToken);
    }

    async Task StartCallCoreAsync(CancellationToken cancellationToken = default)
    {
        if (_interaction.Type is not InteractionType.Connected)
            return;

        var connection = GetConnection();
        if (connection is null)
        {
            StopSession();
            SetState(InteractionType.None, () => { });
            return;
        }

        var sessionToken = _sessionCts?.Token ?? CancellationToken.None;
        var callingCts = CancellationTokenSource.CreateLinkedTokenSource(
            sessionToken,
            cancellationToken
        );

        SetState(InteractionType.Calling, callingCts.Cancel);

        var callMaintainer = new CallMaintainer(connection, _criticalSignalTime);

        _ = callMaintainer.MaintainCallAsync(_useVideo, callingCts.Token);
        _ = callMaintainer
            .WhenReceivedCallPingAsync(callingCts.Token)
            .ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully == false || State is not InteractionType.Calling)
                    return;

                StartSpeaking(connection, callMaintainer);
            });
    }

    public void AcceptCall()
    {
        if (State is not InteractionType.ReceivingCall)
            return;

        var connection = GetConnection();
        if (connection is null)
            return;

        _useVideo = _incomingIsVideoCall;
        Console.WriteLine(
            $"[VIDEO] AcceptCall({contactId}): _incomingIsVideoCall={_incomingIsVideoCall} → _useVideo={_useVideo}"
        );
        if (_incomingIsVideoCall)
            videoCallState.Open(contactId);

        var callMaintainer = new CallMaintainer(connection, _criticalSignalTime);
        StartSpeaking(connection, callMaintainer);
    }

    public void DeclineCall()
    {
        if (State is not InteractionType.ReceivingCall)
            return;

        var connection = GetConnection();
        if (connection is null)
            return;

        using var channel = new RtcConnectionMessageChannel(connection);
        _ = channel.Writer.WriteAsync(new RtcMessage(RtcMessageType.RejectCall));

        SetState(InteractionType.Connected, () => { });
        ListenIncomingCall(connection);
    }

    public async Task EndCall()
    {
        if (_interaction.Type is not InteractionType.Speaking and not InteractionType.Calling)
            return;

        var connection = GetConnection();
        _interaction.Cancel();

        if (videoCallState.ContactId == contactId)
            videoCallState.Close();

        if (connection is null)
        {
            SetState(InteractionType.None, () => { });
            return;
        }

        await SendMessageAsync(string.Empty, RtcMessageType.RejectCall);

        SetState(InteractionType.Connected, () => { });
        ListenIncomingCall(connection);
    }

    public async Task SendMessageAsync(
        string text,
        RtcMessageType messageType = RtcMessageType.User,
        CancellationToken cancellationToken = default
    )
    {
        var connection = GetConnection();
        if (connection is null)
            return;

        await using var chat = new RtcConnectionMessageChannel(connection);
        await chat.Writer.WriteAsync(new RtcMessage(messageType, text), cancellationToken);
    }

    void ListenIncomingCall(RtcConnection connection)
    {
        var sessionToken = _sessionCts?.Token;
        if (sessionToken is null)
            return;

        var callMaintainer = new CallMaintainer(connection, _criticalSignalTime);
        _ = callMaintainer
            .WhenReceivedCallPingAsync(sessionToken.Value)
            .ContinueWith(
                t =>
                {
                    if (t.IsCompletedSuccessfully == false)
                        return;

                    if (State is InteractionType.Connected)
                    {
                        _incomingIsVideoCall = t.Result.IsVideoCall;
                        Console.WriteLine(
                            $"[VIDEO] ListenIncomingCall({contactId}): ping received isVideoCall={_incomingIsVideoCall}"
                        );
                        SetState(InteractionType.ReceivingCall, () => { });
                    }
                },
                sessionToken.Value
            );
    }

    void StartSpeaking(RtcConnection connection, CallMaintainer callMaintainer)
    {
        var sessionToken = _sessionCts?.Token ?? CancellationToken.None;
        var stopCallCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);

        _ = callMaintainer.MaintainCallAsync(_useVideo, stopCallCts.Token);
        SetState(
            InteractionType.Speaking,
            () =>
            {
                stopCallCts.Cancel();
                _ = DisableMediaAsync(connection);
            }
        );

        _ = _useVideo ? EnableVideoMediaAsync(connection) : EnableMediaAsync(connection);
        Console.WriteLine(
            $"[VIDEO] StartSpeaking({contactId}): _useVideo={_useVideo} → EnableMedia called"
        );

        _ = callMaintainer
            .WhenCallStoppedAsync(stopCallCts.Token)
            .ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully == false || State is not InteractionType.Speaking)
                    return;

                stopCallCts.Cancel();
                _ = DisableMediaAsync(connection);
                if (videoCallState.ContactId == contactId)
                    videoCallState.Close();
                SetState(InteractionType.Connected, () => { });
                ListenIncomingCall(connection);
            });
    }

    void SetState(InteractionType newState, Action cancelInteraction)
    {
        _interaction.Cancel();
        _interaction = new(newState, cancelInteraction);
        _stateChanged.Invoke();
    }

    void StartSession(RtcConnection connection)
    {
        StopSession();
        _sessionCts = new CancellationTokenSource();
        _connectionStateSubscription = connection.StateChanged.Subscribe(state =>
        {
            if (state is "disconnected" or "failed" or "closed")
                _ = peerConnector.ClosePeerConnectionAsync(contactId);
        });
        ListenIncomingCall(connection);
    }

    void StopSession()
    {
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        _sessionCts = null;
        _connectionStateSubscription?.Dispose();
        _connectionStateSubscription = null;
    }

    void StartSyncLoop()
    {
        _syncCts?.Cancel();
        _syncCts = new CancellationTokenSource();
        _syncTask = Task.Run(
            async () =>
            {
                while (!_syncCts.IsCancellationRequested)
                {
                    try
                    {
                        var existingConnection = GetConnection();

                        if (
                            State
                                is InteractionType.Connected
                                    or InteractionType.Calling
                                    or InteractionType.ReceivingCall
                                    or InteractionType.Speaking
                            && existingConnection is null
                        )
                        {
                            StopSession();
                            SetState(InteractionType.None, () => { });
                        }
                        else if (
                            State < InteractionType.Connected
                            && existingConnection is not null
                        )
                        {
                            StartSession(existingConnection);
                            SetState(InteractionType.Connected, () => { });
                        }

                        await Task.Delay(250, _syncCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            },
            _syncCts.Token
        );
    }

    RtcConnection? GetConnection() => peerConnector.CurrentConnections.GetValueOrDefault(contactId);

    static async Task EnableMediaAsync(RtcConnection connection)
    {
        await connection.SetMediaStateAsync(
            new MediaState(new(true, true), new MediaPartState(false, false))
        );
    }

    static async Task EnableVideoMediaAsync(RtcConnection connection)
    {
        await connection.SetMediaStateAsync(
            new MediaState(new(true, true), new MediaPartState(true, true))
        );
    }

    static async Task DisableMediaAsync(RtcConnection connection)
    {
        await connection.SetMediaStateAsync(
            new MediaState(new(false, false), new MediaPartState(false, false))
        );
    }
}
