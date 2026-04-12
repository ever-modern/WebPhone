using System.Collections.Concurrent;
using System.Diagnostics;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using WebPhone.Components;
using WebPhone.Services.Background;
using WebPhone.Services.Channels;
using WebPhone.Services.Connectivity;
using WebPhone.Services.Data;

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
    FinishedConnecting,
    Connected,
    Calling,
    ReceivingCall,
    CallStarted,
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
                var connectionAgent = await peerConnector.GetPeerConnectionAsync(contact.Id);
                if (_callListeners.ContainsKey(contact.Id) is false)
                {
                    var maintenanceListener = new CallMaintainer(connectionAgent);
                    Subscription sub = maintenanceListener.SubscribeForCallMaintenance(
                        () =>
                            interactionState.IsCalling
                                ? RtcMessageType.CallRequest
                                : RtcMessageType.MaintainingCall,
                        CancellationToken.None
                    );
                    _callListeners[contact.Id] = sub;
                }

                if (interactionState.IsCallActive)
                {
                    await connectionAgent.EnableAudioInputAsync();
                    await connectionAgent.EnableAudioOutputAsync();
                }
            }
            else if (_callListeners.ContainsKey(contact.Id) is true)
            {
                var connectionAgent = await peerConnector.GetPeerConnectionAsync(contact.Id);

                _callListeners[contact.Id].Dispose();
                _callListeners.TryRemove(contact.Id, out var _);

                await connectionAgent.DisableAudioInputAsync();
                await connectionAgent.DisableVideoInputAsync();
                await connectionAgent.DisableAudioOutputAsync();
                await connectionAgent.DisableVideoOutputAsync();
            }
        }

        _stateChanged.Invoke();
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
                    InteractionType.Speaking or InteractionType.CallStarted => new ContactState(
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
                    InteractionType.Connected or InteractionType.FinishedConnecting =>
                        new ContactState(
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

    void SetInteraction(string contactId, InteractionType interactionType, Action stopAction)
    {
        _interactions[contactId] = new ContactInteraction(interactionType, stopAction);
        StateHasChanged();
    }

    void Connect(string peerId)
    {
        var connectingCts = new CancellationTokenSource();

        SetInteraction(peerId, InteractionType.Connecting, connectingCts.Cancel);

        _ = Task.Run(async () =>
        {
            var connectionAgent = await peerConnector
                .GetPeerConnectionAsync(peerId, connectingCts.Token)
                .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);

            if (connectionAgent is null)
            {
                SetInteraction(peerId, InteractionType.None, () => { });
                return;
            }

            SetInteraction(peerId, InteractionType.FinishedConnecting, () => { });
        });
    }

    public void Dispose() => OnDisposed();
}
