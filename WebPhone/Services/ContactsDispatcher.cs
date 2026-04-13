using System.Collections.Concurrent;
using System.Diagnostics;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using WebPhone.Components;
using WebPhone.Messages;
using WebPhone.Services.Background;
using WebPhone.Services.Channels;
using WebPhone.Services.Data;

namespace WebPhone.Services;

public record ContactState(
    Contact Contact,
    InteractionState InteractionState,
    ContactActions AvailableActions
)
{
    public ContactState Then(Action action) { action(); return this; }
};

public record PhoneState(IReadOnlyList<ContactState> Contacts);

public enum InteractionType
{
    None,
    Connecting,
    Connected,
    Calling,
    ReceivingCall,
    Speaking,
}

public record struct ContactInteraction(InteractionType Type, Action Stop);



public sealed class ContactsDispatcher(
    PeerConnector peerConnector,
    ContactsRepository contactsRepository,
    BackendClient backendClient
) : IDisposable
{
    IReadOnlyList<ContactState> _contacts = [];
    readonly EventSource _stateChanged = new();
    public INotifier StateChanged => _stateChanged;

    public PhoneState State => _state;
    PhoneState _state = new([]);

    readonly ConcurrentDictionary<string, Subscription> _callMaintainers = [];
    readonly ConcurrentDictionary<string, Subscription> _incomingCallListeners = [];
    readonly ConcurrentDictionary<string, ContactInteraction> _interactions = [];
    readonly ConcurrentDictionary<string, List<ChatMessage>> _chatByContact = [];
    readonly SemaphoreSlim _refreshLock = new(1, 1);

    event Action OnDisposed = () => { };

    ContactActions CreateDefaultActions(Contact contact) =>
        new(
            ToggleFavorite: async () =>
                _ = contactsRepository.ToggleFavoriteAsync(contact.Id, contact.Name),
            SetNickname: async (nickname) =>
                _ = contactsRepository.SetNicknameAsync(contact.Id, nickname),
            Notify: () => _ = backendClient.NotifyAsync(contact.Id, null),
            Disconnect: () =>
                _ = peerConnector
                    .ClosePeerConnectionAsync(contact.Id)
                    .ContinueWith((_) => StateHasChanged())
        );

    public Task NotifySelfAsync(
        string? message = null,
        CancellationToken cancellationToken = default
    ) => backendClient.NotifyAsync(null, message, cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var cts = new CancellationTokenSource();

        OnDisposed += contactsRepository.StateChanged.Subscribe(() => _ = RefreshState()).Dispose;
        OnDisposed += peerConnector.StateChanged.Subscribe(() => _ = RefreshState()).Dispose;
        OnDisposed += cts.Cancel;

        await RefreshState().ConfigureAwait(false);
    }

    async Task RefreshState()
    {
        await _refreshLock.WaitAsync();
        try
        {
            var newState = await CalculateStateAsync();
            _state = newState;

            StateHasChanged();

            foreach (var (contact, _, _) in newState.Contacts)
            {
                var (interactionState, cancelInteraction) = _interactions.TryGetValue(
                    contact.Id,
                    out var interaction
                )
                    ? interaction
                    : new ContactInteraction(InteractionType.None, () => { });

                if (
                    interactionState
                    is InteractionType.Connected
                        or InteractionType.FinishedConnecting
                )
                {
                    
                }
                else if (_callMaintainers.ContainsKey(contact.Id) is true)
                {
                    var connectionAgent = await peerConnector.GetPeerConnectionAsync(contact.Id);

                    _callMaintainers[contact.Id].Dispose();
                    _callMaintainers.TryRemove(contact.Id, out var _);

                   

                    if (
                        _interactions.TryGetValue(contact.Id, out var interaction)
                        && (
                            interaction.Type
                            is InteractionType.CallStarted
                                or InteractionType.Speaking
                        )
                    )
                    {
                        _interactions[contact.Id] = new ContactInteraction(
                            InteractionType.Connected,
                            () => { }
                        );
                    }
                }
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    async Task<PhoneState> CalculateStateAsync()
    {
        var contacts = contactsRepository.Contacts;
        var connections = peerConnector.CurrentConnections;

        var contactStates = contacts
            .Select(contact =>
            {
                var chat = _chatByContact.TryGetValue(contact.Id, out var knownChat)
                    ? knownChat.ToList()
                    : [];

                var interactionType = _interactions.TryGetValue(contact.Id, out var interaction)
                    ? interaction.Type
                    : (
                        connections.ContainsKey(contact.Id)
                            ? InteractionType.Connected
                            : InteractionType.None
                    );

                var cancel = interaction.Stop ?? (() => { });

                ContactState contactState = interactionType switch
                {
                    InteractionType.Connecting => HandleConnectingState(contact, chat, cancel),
                    InteractionType.Calling => HandleCallingState(contact, chat, cancel),
                    InteractionType.ReceivingCall => HandleReceivingCallState(contact, chat, cancel),
                    InteractionType.Speaking or InteractionType.CallStarted => HandleSpeakingState(contact, chat, cancel),
                    InteractionType.Connected or InteractionType.FinishedConnecting => HandleConnectedState(contact, chat),
                    _ => HandleDisconnectedState(contact, chat),
                };

                return contactState;
            })
            .ToList();

        return new PhoneState(contactStates);
    }

    ContactState HandleDisconnectedState(Contact contact, List<ChatMessage> chat)
    {
        RemoveIncomingCallListener(contact.Id);
        return new ContactState(
            contact,
            InteractionState: new(Chat: chat),
            CreateDefaultActions(contact) with
            {
                Connect = () =>
                {
                    _interactions[contact.Id] = new ContactInteraction(
                        InteractionType.Connecting,
                        () => { }
                    );
                    Connect(contact.Id);
                    StateHasChanged();
                },
                Disconnect = null,
            }
        );
    }

    ContactState HandleConnectedState(Contact contact, List<ChatMessage> chat)
    {
        _ = EnsureIncomingCallListenerAsync(contact.Id);
        return new ContactState(
            contact,
            InteractionState: new(
                Chat: chat,
                IsConnected: true,
                ConnectionState: "Connected",
                ChatReady: true
            ),
            CreateDefaultActions(contact) with
            {
                SendMessage = text => _ = SendUserMessageAsync(contact.Id, text),
                StartCall = () =>
                {
                    _interactions[contact.Id] = new ContactInteraction(
                        InteractionType.Calling,
                        () => { }
                    );
                    StateHasChanged();
                },
            }
        );
    }

    ContactState HandleSpeakingState(Contact contact, List<ChatMessage> chat, Action cancel)
    {
        return new ContactState(
            contact,
            InteractionState: new(
                Chat: chat,
                IsConnected: true,
                IsCallActive: true,
                ChatReady: true,
                ConnectionState: "In call"
            ),
            CreateDefaultActions(contact) with
            {
                Connect = null,
                SendMessage = text => _ = SendUserMessageAsync(contact.Id, text),
                EndCall = () =>
                {
                    cancel();
                    _interactions.TryRemove(contact.Id, out var _);
                    StateHasChanged();
                },
            }
        );
    }

    ContactState HandleReceivingCallState(Contact contact, List<ChatMessage> chat, Action cancel)
    {
        return new ContactState(
            contact,
            InteractionState: new(
                Chat: chat,
                IsConnected: true,
                ChatReady: true,
                HasIncomingCall: true,
                ConnectionState: "Incoming call"
            ),
            CreateDefaultActions(contact) with
            {
                Connect = null,
                SendMessage = text => _ = SendUserMessageAsync(contact.Id, text),
                AcceptCall = () =>
                {
                    _ = SendRtcMessageAsync(contact.Id, RtcMessageType.AcceptCall);
                    SetInteraction(contact.Id, InteractionType.CallStarted, () => { });
                },
                DeclineCall = () =>
                {
                    cancel();
                    _interactions.TryRemove(contact.Id, out var _);
                    StateHasChanged();
                },
            }
        );
    }

    ContactState HandleCallingState(Contact contact, List<ChatMessage> chat, Action cancel)
    {
        return new ContactState(
            contact,
            InteractionState: new(
                Chat: chat,
                IsConnected: true,
                IsCalling: true,
                ChatReady: true,
                ConnectionState: "Calling"
            ),
            CreateDefaultActions(contact) with
            {
                Connect = null,
                SendMessage = text => _ = SendUserMessageAsync(contact.Id, text),
                CancelCall = () =>
                {
                    cancel();
                    _interactions.TryRemove(contact.Id, out var _);
                    StateHasChanged();
                },
            }
        );
    }

    ContactState HandleConnectingState(Contact contact, List<ChatMessage> chat, Action cancel)
    {
        return new ContactState(
            contact,
            InteractionState: new(Chat: chat, IsConnecting: true),
            CreateDefaultActions(contact) with
            {
                CancelConnect = () =>
                {
                    cancel();
                    _interactions.TryRemove(contact.Id, out var _);
                    StateHasChanged();
                },
                Connect = null,
                Disconnect = null,
            }
        );
    }

    void StateHasChanged() => _ = RefreshState();

    async Task EnsureCallMaintenanceAsync(string contactId, InteractionState interactionState)
    {
        if (_callMaintainers)
        var connectionAgent = await peerConnector.GetPeerConnectionAsync(contactId);
        var maintenanceListener = new CallMaintainer(connectionAgent);
        Subscription sub = maintenanceListener.SubscribeForCallMaintenance(
            () =>
                interactionState == InteractionType.

                    ? RtcMessageType.CallRequest
                    : RtcMessageType.MaintainingCall,
            CancellationToken.None
        );
        _callMaintainers[contactId] = sub;

        if (interactionState.IsCallActive)
        {
            await connectionAgent.EnableAudioInputAsync();
            await connectionAgent.EnableAudioOutputAsync();
        }
    }

    async Task EnsureIncomingCallListenerAsync(string contactId)
    {
        if (_incomingCallListeners.ContainsKey(contactId))
            return;

        var connection = await peerConnector.GetPeerConnectionAsync(contactId);
        

        _incomingCallListeners[contactId] = new Subscription(() =>
        {
            subscription.Dispose();
            channel.Dispose();
        });
    }

    void RemoveIncomingCallListener(string contactId)
    {
        if (_incomingCallListeners.TryRemove(contactId, out var listener))
            listener.Dispose();
    }

    async Task SendRtcMessageAsync(string contactId, RtcMessageType messageType)
    {
        var connection = await peerConnector.GetPeerConnectionAsync(contactId);
        await using var channel = new RtcConnectionMessageChannel(connection);
        await channel.Writer.WriteAsync(new RtcMessage(messageType, null));
    }

    async Task SendUserMessageAsync(string contactId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var connection = await peerConnector.GetPeerConnectionAsync(contactId);
        await using var channel = new RtcConnectionMessageChannel(connection);
        await channel.Writer.WriteAsync(new RtcMessage(RtcMessageType.User, text));

        var chat = _chatByContact.GetOrAdd(contactId, _ => []);
        lock (chat)
        {
            chat.Add(new ChatMessage("self", text, true));
        }

        _ = RefreshState();
    }

    void SetInteraction(string contactId, InteractionType interactionType, Action stopAction)
    {
        if (_interactions.TryGetValue(contactId, out var existingInteraction))
        {
            if (existingInteraction.Type != interactionType)
            {
                existingInteraction.Stop();
            }
            else 
            {
                return;
            }
        }
        _interactions[contactId] = new ContactInteraction(interactionType, stopAction);
        StateHasChanged();
    }

    

    public void Dispose() => OnDisposed();
}
