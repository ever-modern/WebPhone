using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using WebPhone.Components;
using WebPhone.Messages;
using WebPhone.Services.Channels;
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
    Connected,
    Calling,
    ReceivingCall,
    Speaking,
}

public sealed class ContactsDispatcher(
    PeerConnector peerConnector,
    ContactsRepository contactsRepository,
    BackendClient backendClient,
    VideoCallState videoCallState
) : IDisposable
{
    readonly EventSource _stateChanged = new();
    public INotifier StateChanged => _stateChanged;

    public PhoneState State => _state;
    PhoneState _state = new([]);

    readonly Dictionary<string, ContactContext> _contexts = [];
    bool _isStarted;

    event Action OnDisposed = () => { };

    sealed class IncomingChatReader(CancellationTokenSource cancellation, Task task)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; } = task;
    }

    sealed class ContactContext(ContactManager manager, Subscription stateSubscription)
        : IDisposable
    {
        public ContactManager Manager { get; } = manager;
        public List<ChatMessage> Chat { get; } = [];
        public object ChatLock { get; } = new();
        public RtcConnection? ChatConnection { get; set; }

        readonly Subscription _stateSubscription = stateSubscription;

        public void Dispose()
        {
            _stateSubscription.Dispose();
            Manager.Dispose();
        }
    }

    readonly Dictionary<string, IncomingChatReader> _incomingChatReaders = [];

    ContactActions CreateDefaultActions(Contact contact) =>
        new(
            ToggleFavorite: () =>
                _ = contactsRepository.ToggleFavoriteAsync(contact.Id, contact.Name),
            SetNickname: (nickname) =>
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
        if (_isStarted)
            return;

        _isStarted = true;

        OnDisposed += contactsRepository.StateChanged.Subscribe(StateHasChanged).Dispose;
        OnDisposed += peerConnector.StateChanged.Subscribe(StateHasChanged).Dispose;

        StateHasChanged();
        await Task.CompletedTask;
    }

    void EnsureContactContexts()
    {
        var contactIds = contactsRepository
            .Contacts.Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var contact in contactsRepository.Contacts)
        {
            if (_contexts.ContainsKey(contact.Id))
                continue;

            var manager = new ContactManager(peerConnector, contact.Id, videoCallState);
            var subscription = manager.StateChanged.Subscribe(StateHasChanged);
            var context = new ContactContext(manager, subscription);
            _contexts[contact.Id] = context;
            _ = manager.StartAsync(CancellationToken.None);
        }

        foreach (var removed in _contexts.Keys.Where(id => !contactIds.Contains(id)).ToList())
        {
            StopIncomingChatReader(removed);
            _contexts[removed].Dispose();
            _contexts.Remove(removed);
        }
    }

    void EnsureIncomingChatReaders()
    {
        foreach (var (contactId, context) in _contexts)
        {
            if (peerConnector.CurrentConnections.TryGetValue(contactId, out var connection))
            {
                if (ReferenceEquals(context.ChatConnection, connection))
                    continue;

                StopIncomingChatReader(contactId);
                context.ChatConnection = connection;
                StartIncomingChatReader(contactId, context, connection);
                continue;
            }

            context.ChatConnection = null;
            StopIncomingChatReader(contactId);
        }
    }

    void StartIncomingChatReader(string contactId, ContactContext context, RtcConnection connection)
    {
        var cts = new CancellationTokenSource();
        var task = Task.Run(
            async () =>
            {
                try
                {
                    await using var channel = new RtcConnectionMessageChannel(connection);
                    using var reader = channel.Subscribe(msg => msg.Type is RtcMessageType.User);

                    await foreach (var message in reader.ReadAllAsync(cts.Token))
                    {
                        if (string.IsNullOrWhiteSpace(message.Payload))
                            continue;

                        lock (context.ChatLock)
                        {
                            context.Chat.Add(new ChatMessage(contactId, message.Payload!, false));
                        }

                        StateHasChanged();
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            },
            cts.Token
        );

        _incomingChatReaders[contactId] = new IncomingChatReader(cts, task);
    }

    void StopIncomingChatReader(string contactId)
    {
        if (_incomingChatReaders.TryGetValue(contactId, out var reader) is false)
            return;

        reader.Cancellation.Cancel();
        reader.Cancellation.Dispose();
        _incomingChatReaders.Remove(contactId);
    }

    PhoneState CalculateState()
    {
        var contactStates = contactsRepository
            .Contacts.Select(contact =>
            {
                if (_contexts.TryGetValue(contact.Id, out var context) is false)
                    return BuildDisconnectedState(contact, []);

                List<ChatMessage> chat;
                lock (context.ChatLock)
                {
                    chat = context.Chat.ToList();
                }

                var effectiveState = context.Manager.State;
                if (
                    effectiveState is InteractionType.None
                    && peerConnector.CurrentConnections.ContainsKey(contact.Id)
                )
                {
                    effectiveState = InteractionType.Connected;
                }

                return effectiveState switch
                {
                    InteractionType.Connecting => BuildConnectingState(contact, context, chat),
                    InteractionType.Connected => BuildConnectedState(contact, context, chat),
                    InteractionType.Calling => BuildCallingState(contact, context, chat),
                    InteractionType.ReceivingCall => BuildReceivingCallState(
                        contact,
                        context,
                        chat
                    ),
                    InteractionType.Speaking => BuildSpeakingState(contact, context, chat),
                    _ => BuildDisconnectedState(contact, chat),
                };
            })
            .ToList();

        return new PhoneState(contactStates);
    }

    ContactState BuildDisconnectedState(Contact contact, List<ChatMessage> chat)
    {
        return new ContactState(
            contact,
            InteractionState: new(Chat: chat),
            CreateDefaultActions(contact) with
            {
                Connect = async () =>
                {
                    if (_contexts.TryGetValue(contact.Id, out var context))
                    {
                        var connected = await context
                            .Manager.ConnectAsync()
                            .ContinueWith(t => t.IsCompletedSuccessfully);
                        return connected;
                    }

                    return false;
                },
                Disconnect = null,
            }
        );
    }

    ContactState BuildConnectedState(
        Contact contact,
        ContactContext context,
        List<ChatMessage> chat
    )
    {
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
                SendMessage = text => SendUserMessage(contact.Id, text),
                StartCall = () => _ = context.Manager.StartCallAsync(),
                StartVideoCall = () =>
                {
                    videoCallState.Open(contact.Id);
                    _ = context.Manager.StartVideoCallAsync();
                },
                Disconnect = () => _ = context.Manager.DisconnectAsync(),
            }
        );
    }

    ContactState BuildSpeakingState(Contact contact, ContactContext context, List<ChatMessage> chat)
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
                SendMessage = text => SendUserMessage(contact.Id, text),
                EndCall = () =>
                {
                    if (videoCallState.ContactId == contact.Id)
                        videoCallState.Close();
                    context.Manager.EndCall();
                },
                Disconnect = () => _ = context.Manager.DisconnectAsync(),
            }
        );
    }

    ContactState BuildReceivingCallState(
        Contact contact,
        ContactContext context,
        List<ChatMessage> chat
    )
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
                SendMessage = text => SendUserMessage(contact.Id, text),
                AcceptCall = context.Manager.AcceptCall,
                DeclineCall = context.Manager.DeclineCall,
                Disconnect = () => _ = context.Manager.DisconnectAsync(),
            }
        );
    }

    ContactState BuildCallingState(Contact contact, ContactContext context, List<ChatMessage> chat)
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
                SendMessage = text => SendUserMessage(contact.Id, text),
                CancelCall = () => _ = context.Manager.EndCall(),
                Disconnect = () => _ = context.Manager.DisconnectAsync(),
            }
        );
    }

    ContactState BuildConnectingState(
        Contact contact,
        ContactContext context,
        List<ChatMessage> chat
    )
    {
        return new ContactState(
            contact,
            InteractionState: new(Chat: chat, IsConnecting: true),
            CreateDefaultActions(contact) with
            {
                CancelConnect = context.Manager.StopConnecting,
                Connect = null,
                Disconnect = null,
            }
        );
    }

    void StateHasChanged()
    {
        EnsureContactContexts();
        EnsureIncomingChatReaders();
        _state = CalculateState();
        _stateChanged.Invoke();
    }

    void SendUserMessage(string contactId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (_contexts.TryGetValue(contactId, out var context) is false)
            return;

        _ = context.Manager.SendMessageAsync(text);
        lock (context.ChatLock)
        {
            context.Chat.Add(new ChatMessage("self", text, true));
        }
        StateHasChanged();
    }

    public void Dispose()
    {
        foreach (var contactId in _incomingChatReaders.Keys.ToList())
            StopIncomingChatReader(contactId);

        foreach (var context in _contexts.Values)
            context.Dispose();

        _contexts.Clear();
        OnDisposed();
    }
}
