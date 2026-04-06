using System.Collections.Concurrent;
using WebPhone.Registration;

namespace WebPhone.Services;

public class ContactsRepository(IMessagesChannel messagesChannel, ILocalStore localStore) : IAsyncDisposable
{
    private const string FavoriteContactsStorageKey = "favorite-contacts";
    private readonly ConcurrentDictionary<string, Contact> _presences = [];
    private readonly ConcurrentDictionary<string, string> _favorites = [];
    private CancellationTokenSource? _cts;
    private Task? _readerTask;

    public IReadOnlyList<Contact> Contacts { get; private set; } = [];

    public event Action? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var favorites = await localStore.GetAsync<List<FavoriteContact>>(FavoriteContactsStorageKey, cancellationToken) ?? [];
        foreach (var f in favorites)
        {
            if (!string.IsNullOrWhiteSpace(f.Id))
                _favorites[f.Id] = f.Name;
        }
        RebuildContacts();
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _readerTask = ReadPresenceAsync(_cts.Token);
    }

    public void UpdateConnectionState(string userId, RtcConnectionState state)
    {
        if (_presences.TryGetValue(userId, out var user))
        {
            _presences[userId] = user with { ConnectionState = state };
            RebuildContacts();
            StateChanged?.Invoke();
        }
    }

    public async Task ToggleFavoriteAsync(string userId, string userName, CancellationToken cancellationToken = default)
    {
        if (!_favorites.TryRemove(userId, out _))
            _favorites[userId] = userName;

        await SaveFavoritesAsync(cancellationToken);
        RebuildContacts();
        StateChanged?.Invoke();
    }

    private async Task ReadPresenceAsync(CancellationToken ct)
    {
        using var reader = messagesChannel.Subscribe(m => m.Type == MessageType.Presence);
        await foreach (var message in reader.ReadAllAsync(ct))
        {
            var payload = message.SpecifyPayload<PresencePayload>();
            if (payload is null) continue;

            var prev = _presences.TryGetValue(message.SenderClientId, out var existing) ? existing : null;
            _presences[message.SenderClientId] = new Contact(
                message.SenderClientId,
                payload.Payload.Name,
                DateTimeOffset.UtcNow,
                prev?.ConnectionState ?? RtcConnectionState.New,
                _favorites.ContainsKey(message.SenderClientId));

            PrunePresence();
            RebuildContacts();
            StateChanged?.Invoke();
        }
    }

    private void PrunePresence()
    {
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-30);
        foreach (var (key, _) in _presences.Where(u => u.Value.LastSeen < cutoff).ToArray())
            _presences.TryRemove(key, out _);
    }

    private void RebuildContacts()
    {
        var merged = new Dictionary<string, Contact>(_presences.Count + _favorites.Count);

        foreach (var (id, contact) in _presences)
            merged[id] = contact with { IsFavorite = _favorites.ContainsKey(id) };

        foreach (var (id, name) in _favorites)
        {
            if (!merged.ContainsKey(id))
                merged[id] = new Contact(id, name, DateTimeOffset.MinValue, RtcConnectionState.Closed, true);
        }

        Contacts = [.. merged.Values
            .OrderByDescending(u => u.IsFavorite)
            .ThenByDescending(u => u.LastSeen)
            .ThenBy(u => u.Name)];
    }

    private async Task SaveFavoritesAsync(CancellationToken cancellationToken = default)
    {
        var list = _favorites.Select(x => new FavoriteContact(x.Key, x.Value)).ToList();
        await localStore.SetAsync(FavoriteContactsStorageKey, list, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_readerTask is not null)
            await _readerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _cts?.Dispose();
    }
}
