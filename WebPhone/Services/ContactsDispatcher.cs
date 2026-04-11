using System.Collections.Concurrent;
using System.Diagnostics;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using WebPhone.Components;

namespace WebPhone.Services;

public record ContactState(
    Contact Contact,
    InteractionState InteractionState,
    ContactActions AvailableActions
);

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

    readonly ConcurrentDictionary<string, Subscription> _callListeners = [];
    readonly ConcurrentDictionary<string, ContactInteraction> _interactions = [];

    event Action OnDisposed = () => { };

    ContactActions CreateDefaultActions(Contact contact) =>
        new(
            ToggleFavorite: async () =>
                _ = contactsRepository
                    .ToggleFavoriteAsync(contact.Id, contact.Name),
            SetNickname: async (nickname) =>
                _ = contactsRepository
                    .SetNicknameAsync(contact.Id, nickname),
            Notify: (contactId, message) =>
                _ = backendClient
                    .NotifyAsync(contactId, message),
            Disconnect: () =>
                _ = peerConnector
                    .ClosePeerConnectionAsync(contact.Id)
                    .ContinueWith((_) => StateHasChanged())
        );

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var cts = new CancellationTokenSource();

        OnDisposed += contactsRepository
            .StateChanged.Subscribe(() => RefreshState().ConfigureAwait(false))
            .Dispose;
        OnDisposed += peerConnector
            .StateChanged.Subscribe(() => RefreshState().ConfigureAwait(false))
            .Dispose;
        OnDisposed += cts.Cancel;

        await RefreshState().ConfigureAwait(false);
    }

    async Task RefreshState()
    {
        var newState = await CalculateStateAsync();
        _state = newState;

        foreach (var (contact, interactionState, _) in newState.Contacts)
        {
            if (interactionState is { IsCallActive: true } or { IsCalling: true })
            {
                if (_callListeners.ContainsKey(contact.Id) is false)
                {
                    Subscription sub = await SubscribeForCallMaintenanceAsync(
                        contact,
                        interactionState
                    );
                    _callListeners[contact.Id] = sub;
                }
            }
            else if (_callListeners.ContainsKey(contact.Id) is true)
            {
                _callListeners[contact.Id].Dispose();
                _callListeners.TryRemove(contact.Id, out var _);
            }
        }

        _stateChanged.Invoke();
    }

    private async Task<Subscription> SubscribeForCallMaintenanceAsync(
        Contact contact,
        InteractionState interactionState
    )
    {
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var connectionAgent = await peerConnector.GetPeerConnectionAsync(contact.Id);
        var incomingChannel = new RtcConnectionMessageChannel(connectionAgent);
        var receivingCts = new CancellationTokenSource();
        var incomingMaintenance = incomingChannel.WhileReceiving(
            m => m.Type == RtcMessageType.MaintainingCall,
            TimeSpan.FromMilliseconds(500),
            receivingCts.Token
        );

        var callCts = CancellationTokenSource.CreateLinkedTokenSource(incomingMaintenance);
        var messageType = interactionState.IsCallActive
            ? RtcMessageType.MaintainingCall
            : RtcMessageType.CallRequest;
        _ = Task.Run(async () =>
        {
            await using var channel = new RtcConnectionMessageChannel(connectionAgent);
            while (await timer.WaitForNextTickAsync(callCts.Token))
            {
                await channel.Writer.WriteAsync(new(messageType, null));
            }
        });
        var sub = new Subscription(() =>
        {
            receivingCts.Cancel();
            incomingChannel.Dispose();
            connectionAgent.DisableAudioInputAsync().ConfigureAwait(false);
            connectionAgent.DisableAudioOutputAsync().ConfigureAwait(false);
            connectionAgent.DisableVideoInputAsync().ConfigureAwait(false);
            connectionAgent.DisableVideoOutputAsync().ConfigureAwait(false);
        });
        return sub;
    }

    async Task<PhoneState> CalculateStateAsync()
    {
        var contacts = contactsRepository.Contacts;
        var connections = peerConnector.CurrentConnections;

        var contactStates = contacts
            .Select(c =>
            {
                var chat =
                    _state
                        .Contacts.FirstOrDefault(cs => cs.Contact.Id == c.Id)
                        ?.InteractionState.Chat
                    ?? [];

                var interactionType = _interactions.TryGetValue(c.Id, out var interaction)
                    ? interaction.Type
                    : InteractionType.None;

                var cancel = interaction.Stop ?? (() => { });
                ContactState contactState = interactionType switch
                {
                    InteractionType.Connecting => new ContactState(
                        c,
                        InteractionState: new(Chat: chat, IsConnecting: true),
                        CreateDefaultActions(c) with
                        {
                            CancelConnect = () =>
                            {
                                cancel();
                                _interactions.TryRemove(c.Id, out var _);
                                StateHasChanged();
                            },
                            Connect = null,
                            Disconnect = null,
                        }
                    ),
                    InteractionType.Calling => new ContactState(
                        c,
                        InteractionState: new(Chat: chat, IsConnected: true, IsCalling: true),
                        CreateDefaultActions(c) with
                        {
                            Connect = null,
                            CancelCall = () =>
                            {
                                cancel();
                                _interactions.TryRemove(c.Id, out var _);
                                StateHasChanged();
                            },
                        }
                    ),
                    InteractionType.ReceivingCall => new ContactState(
                        c,
                        InteractionState: new(Chat: chat, IsConnected: true, HasIncomingCall: true),
                        CreateDefaultActions(c) with
                        {
                            Connect = null,
                            DeclineCall = () =>
                            {
                                cancel();
                                _interactions.TryRemove(c.Id, out var _);
                                StateHasChanged();
                            },
                        }
                    ),
                    InteractionType.Speaking => new ContactState(
                        c,
                        InteractionState: new(Chat: chat, IsConnected: true, IsCallActive: true),
                        CreateDefaultActions(c) with
                        {
                            Connect = null,
                            EndCall = () =>
                            {
                                cancel();
                                _interactions.TryRemove(c.Id, out var _);
                                StateHasChanged();
                            },
                        }
                    ),
                    InteractionType.Connected => new ContactState(
                        c,
                        InteractionState: new(
                            Chat: chat,
                            IsConnected: true,
                            ConnectionState: "Connected",
                            ChatReady: true
                        ),
                        CreateDefaultActions(c) with
                        {
                            StartCall = () =>
                            {
                                _interactions[c.Id] = new ContactInteraction(
                                    InteractionType.Calling,
                                    () => { }
                                );
                                StateHasChanged();
                            },
                        }
                    ),
                    _ => new ContactState(
                        c,
                        InteractionState: new(Chat: chat),
                        CreateDefaultActions(c) with
                        {
                            Connect = () =>
                            {
                                _interactions[c.Id] = new ContactInteraction(
                                    InteractionType.Connecting,
                                    () => { }
                                );
                                Connect(c.Id);
                                StateHasChanged();
                            },
                            Disconnect = null,
                        }
                    ),
                };

                return contactState;
            })
            .ToList();

        return new PhoneState(contactStates);
    }

    void StateHasChanged() => _stateChanged.Invoke();

    void SetContactState(string contactId, Func<ContactState, ContactState> newContactState)
    {
        var newContacts = _contacts
            .Select(c => c.Contact.Id == contactId ? newContactState(c) : c)
            .ToList();

        _stateChanged.Invoke();
    }

    void Connect(string peerId)
    {
        var contact = _contacts.FirstOrDefault(c => c.Contact.Id == peerId);
        if (contact is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        SetContactState(
            peerId,
            contact =>
                contact with
                {
                    InteractionState = new(IsConnecting: true),
                    AvailableActions = CreateDefaultActions(contact.Contact) with
                    {
                        CancelConnect = cts.Cancel,
                    },
                }
        );

        _ = Task.Run(async () =>
        {
            var connectionAgent = await peerConnector.GetPeerConnectionAsync(peerId, cts.Token);
            var callCts = new CancellationTokenSource();
            SetContactState(
                peerId,
                contact =>
                    contact with
                    {
                        InteractionState = new(IsConnecting: false, IsConnected: true),
                        AvailableActions = CreateDefaultActions(contact.Contact) with
                        {
                            CancelConnect = null,
                            Disconnect = () => _ = DisconnectAsync(connectionAgent),
                            StartCall = () =>
                                _ = StartCallingAsync(
                                    contact.Contact.Id,
                                    connectionAgent,
                                    callCts.Token
                                ),
                        },
                    }
            );
        });
    }

    async Task DisconnectAsync(RtcConnectionAgent connectionAgent)
    {
        await connectionAgent.DisposeAsync();
    }

    async Task StartCallingAsync(
        string contactId,
        RtcConnectionAgent connectionAgent,
        CancellationToken cancellationToken
    )
    {
        await using var peerChat = new RtcConnectionMessageChannel(connectionAgent);

        using var acceptCallMessagePoller = peerChat.Subscribe(m =>
            m.Type == RtcMessageType.AcceptCall
        );

        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));

        var callRequestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _ = Task.Run(
            async () =>
            {
                while (await timer.WaitForNextTickAsync(callRequestCts.Token))
                {
                    await peerChat.Writer.WriteAsync(
                        new RtcMessage(RtcMessageType.CallRequest, null)
                    );
                }
            },
            cancellationToken
        );

        SetContactState(
            contactId,
            contact =>
                contact with
                {
                    InteractionState = new(
                        IsCalling: true,
                        IsConnected: true,
                        Chat: contact.InteractionState.Chat
                    ),
                    AvailableActions = new(CancelCall: () => callRequestCts.Cancel()),
                }
        );

        var response = await acceptCallMessagePoller.ReadAsync(cancellationToken);

        if (callRequestCts.IsCancellationRequested)
            return;

        callRequestCts.Cancel();

        if (cancellationToken.IsCancellationRequested)
            return;

        await connectionAgent.EnableAudioInputAsync();
        await connectionAgent.EnableAudioOutputAsync();

        SetContactState(
            contactId,
            contact =>
                contact with
                {
                    InteractionState = new(
                        IsCallActive: true,
                        IsConnected: true,
                        ChatReady: true,
                        Chat: contact.InteractionState.Chat
                    ),
                    AvailableActions = CreateDefaultActions(contact.Contact) with
                    {
                        CancelCall = null,
                        EndCall = () => _ = EndCallAsync(contactId, connectionAgent),
                    },
                }
        );
    }

    async Task EndCallAsync(string contactId, RtcConnectionAgent connectionAgent)
    {
        await connectionAgent.DisableAudioInputAsync();
        await connectionAgent.DisableAudioOutputAsync();
        SetContactState(
            contactId,
            contact =>
                contact with
                {
                    InteractionState = new(IsConnected: true, Chat: contact.InteractionState.Chat),
                    AvailableActions = CreateDefaultActions(contact.Contact) with { },
                }
        );
        if (_callListeners.TryGetValue(contactId, out var maintenanceListener))
        {
            maintenanceListener.Dispose();
            _callListeners.TryRemove(contactId, out var _);
        }
    }

    public void Dispose() => OnDisposed();
}

public record User(string Id, string Name);

public record Contact(
    string Id,
    string Name,
    DateTimeOffset LastSeen,
    bool IsFavorite = false,
    string? Nickname = null
) : User(Id, Name);

public record RtcTextMessage(string Text, bool IsSystem);

public record FavoriteContact(string Id, string Name);

public sealed record UserPresence(string UserId, string Name, DateTimeOffset LastSeen);

public sealed record PresencePayload(string Name);

public sealed record HungupPayload(string CallId);

public sealed record ConnectionRequestPayload(string RequestId, WebRtcOffer Offer);

public sealed record AnswerPayload(string RequestId, WebRtcAnswer Answer);

public sealed record InitiateCallPayload(string ConnectionId);

public sealed record CallResponsePayload(string ConnectionId, bool Accepted);
