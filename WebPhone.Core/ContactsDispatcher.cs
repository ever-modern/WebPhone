using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
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
    readonly EventSource<PhoneState> _stateChanged = new();
    public INotifier<PhoneState> StateChanged => _stateChanged;

    Dictionary<string, Entry> _state = [];

    readonly List<IDisposable> _toDispose = [];
    readonly Lock _locker = new();
    
    volatile bool  _started;

    public ContactsDispatcher Start()
    {
        if (_started)
            return this;
        _started = true;
        
        var sub1 = peerConnectionsDispatcher.ConnectionsChange.Subscribe(_ =>
            ResetState()
        );
        var sub2 = contactsRepository.StateChanged.Subscribe(ResetState);
        _toDispose.AddRange(sub1, sub2);
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
            _state.Select(kv =>
                    {
                        ContactManager manager = new(kv.Value.MediaConnection, kv.Key, peerConnectionsDispatcher);
                        return manager;
                    }
                )
                .ToArray()
        );
        _stateChanged.Invoke(newPhoneState);
    }

    Dictionary<string, Entry> CalculateState(out IReadOnlyList<Entry> irrelevantEntries)
    {
        var entriesToRemove = new List<Entry>();

        var state = contactsRepository.Contacts
            .Select(contact =>
                {
                    var connection = peerConnectionsDispatcher.FindReadyConnection(contact.Id);
                    if (connection is null && contact.IsFavorite == false)
                    {
                        return null;
                    }

                    if (_state.TryGetValue(contact.Id, out var existingEntry) && ReferenceEquals(existingEntry.Connection, connection))
                    {
                        return existingEntry;
                    }

                    if (existingEntry is not null)
                    {
                        entriesToRemove.Add(existingEntry);
                    }

                    InteractionState initialState = DateTime.UtcNow - contact.LastSeen > TimeSpan.FromSeconds(10) ? InteractionState.Offline.Instance : InteractionState.Disconnected.Instance;
                    var newEntry = Entry.Create(
                        contact.Id,
                        connection!,
                        loggerFactory,
                        initialState
                    );

                    return newEntry;
                }
            )
            .Where(entry => entry is not null)
            .ToDictionary(entry => entry!.PeerId, entry => entry!);

        irrelevantEntries = entriesToRemove;
        return state;
    }

    record Entry(
        string PeerId,
        IRtcConnection Connection,
        RtcConnectionMessageChannel Channel,
        MediaConnection MediaConnection
    ) : IDisposable
    {
        internal required ObservedValue<InteractionState> State { get; init; }
        public IValueNotifier<InteractionState> StateChanged => State;

        public static Entry Create(string peerId, IRtcConnection connection, ILoggerFactory loggerFactory, InteractionState initialState)
        {
            var logger = loggerFactory.CreateLogger($"MediaConnection-{peerId}");
            var channel = new RtcConnectionMessageChannel(connection);
            var state = new ObservedValue<InteractionState>(initialState);
            var mediaConnection = new MediaConnection(channel, logger, state);
            Entry result = new(
                peerId,
                connection,
                channel,
                mediaConnection
            )
            {
                State = state
            };

            return result;
        }

        public void Dispose() { Connection.Dispose(); }
    }

    public void Dispose()
    {
        foreach (var item in _toDispose)
        {
            item.Dispose();
        }
    }
}
