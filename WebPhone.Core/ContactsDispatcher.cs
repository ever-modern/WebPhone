using System.Threading.Channels;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using EverModern.Threading.Channels;
using EverModern.Threading.Locks;
using Microsoft.Extensions.Logging;
using WebPhone.Channels;
using WebPhone.Data;

namespace WebPhone;

public record PhoneState(
    IReadOnlyList<ContactManager> Contacts
);

public sealed class ContactsDispatcher(
    PeerConnectionsDispatcher peerConnectionsDispatcher,
    ContactsRepository contactsRepository,
    ILoggerFactory loggerFactory
) : IDisposable
{
    readonly ObservedValue<PhoneState> _exposedState = new(new([]));
    public IValueNotifier<PhoneState> State => _exposedState;

    readonly EventSource<string> _oneConnectionChanged = new();

    Dictionary<string, Entry> _state = [];

    readonly List<IDisposable> _toDispose = [];
    readonly Lock _locker = new();

    volatile bool _started;

    public ContactsDispatcher Start()
    {
        if (_started)
            return this;
        _started = true;

        var sub1 = peerConnectionsDispatcher.ConnectionsChange.Subscribe(() =>
            {
                ResetState();
            }
        );
        var sub2 = contactsRepository.StateChanged.Subscribe(() =>
            {
                ResetState();
            }
        );
        var sub3 = _oneConnectionChanged.Subscribe(_ =>
            {
                ResetState();
            }
        );

        _toDispose.AddRange(sub1, sub2, sub3);

        return this;
    }

    void ResetState()
    {
        using var __ = _locker.LockScope();
        _state = CalculateState(out var entriesToRemove);
        foreach (var entry in entriesToRemove)
        {
            entry.Dispose();
        }
        var newPhoneState = new PhoneState(
            [
                .. _state.Select(kv => new ContactManager(
                        kv.Value.Contact,
                        kv.Value.MediaConnection,
                        peerConnectionsDispatcher,
                        contactsRepository
                    )
                ),
            ]
        );

        _exposedState.Change(newPhoneState);
    }

    Dictionary<string, Entry> CalculateState(out IReadOnlyList<Entry> irrelevantEntries)
    {
        var entriesToRemove = new List<Entry>();

        var state = contactsRepository
            .Contacts.Select(contact =>
                {
                    var connection = peerConnectionsDispatcher.FindReadyConnection(contact.Id);

                    if (
                        _state.TryGetValue(contact.Id, out var existingEntry)
                        && (connection is not null && existingEntry.MediaConnection.State.Value is InteractionState.Connected || (connection is null && existingEntry.MediaConnection.State.Value is not InteractionState.Connected))
                    )
                    {
                        return existingEntry;
                    }

                    if (existingEntry is not null)
                    {
                        entriesToRemove.Add(existingEntry);
                    }

                    var newEntry = connection is not null ?
                        CreateEntry(
                            connection!,
                            contact
                        ) :
                        new(contact, new MediaConnection(null, null, new(TellInteractivity(contact.LastSeen))), () => {});

                    return newEntry;
                }
            )
            .Where(entry => entry is not null)
            .ToDictionary(entry => entry!.Contact.Id, entry => entry!);

        irrelevantEntries = entriesToRemove;
        return state;
    }

    Entry CreateEntry(
        IRtcConnection connection,
        Contact contact
    )
    {
        InteractionState initialState = TellInteractivity(contact.LastSeen);
        ObservedValue<InteractionState> state = new(initialState);
        var logger = loggerFactory.CreateLogger($"MediaConnection-{contact.Id}");
        var channel = new RtcConnectionMessageChannel(connection);
        var mediaConnection = new MediaConnection(channel, logger, state).Started();
        var sub = mediaConnection.State.Subscribe(() => _oneConnectionChanged.Invoke(contact.Id));

        return new(
            contact,
            mediaConnection,
            sub.Dispose
        );
    }

    static InteractionState TellInteractivity(DateTimeOffset lastSeen) => DateTimeOffset.UtcNow - lastSeen > TimeSpan.FromSeconds(10) ? InteractionState.Offline.Instance : InteractionState.Disconnected.Instance;

    record Entry(
        Contact Contact,
        MediaConnection MediaConnection,
        Action OnDisposed
    ) : IDisposable
    {
        public void Dispose() { OnDisposed(); }
    }

    public void Dispose()
    {
        foreach (var item in _toDispose)
        {
            item.Dispose();
        }
    }

    class EmptyBroadcastChannel : IBroadcastChannel<RtcMessage, RtcMessage>
    {
        public IChannelSubscription<RtcMessage> Subscribe(Func<RtcMessage, bool> filter) => throw new NotImplementedException();
        public ChannelWriter<RtcMessage> Writer { get; }
    }
}
