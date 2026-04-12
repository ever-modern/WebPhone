using System.Collections.Concurrent;
using EverModern.Events;
using WebPhone.Messages;
using WebPhone.Services.Channels;

namespace WebPhone.Services.Data;

public class ContactsRepository(
    IMessagesChannel messagesChannel,
    ILocalStore localStore,
    NicknamesRepository nicknamesRepository
) : IAsyncDisposable
{
    private const string FavoriteContactsStorageKey = "favorite-contacts";
    private readonly ConcurrentDictionary<string, Contact> _presences = [];
    private readonly ConcurrentDictionary<string, string> _favorites = [];
    private CancellationTokenSource? _cts;
    private Task? _readerTask;

    public IReadOnlyList<Contact> Contacts { get; private set; } = [];

    readonly EventSource _stateChanged = new();
    public INotifier StateChanged => _stateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var favorites =
            await localStore.GetAsync<List<FavoriteContact>>(
                FavoriteContactsStorageKey,
                cancellationToken
            ) ?? [];
        foreach (var f in favorites)
        {
            if (!string.IsNullOrWhiteSpace(f.Id))
                _favorites[f.Id] = f.Name;
        }
        RebuildContacts();
    }

    public void StartTracking()
    {
        _cts = new CancellationTokenSource();
        _readerTask = ReadPresenceAsync(_cts.Token);
    }

    public async Task ToggleFavoriteAsync(
        string userId,
        string userName,
        CancellationToken cancellationToken = default
    )
    {
        if (!_favorites.TryRemove(userId, out _))
            _favorites[userId] = userName;

        await SaveFavoritesAsync(cancellationToken);
        RebuildContacts();
        _stateChanged.Invoke();
    }

    public async Task SetNicknameAsync(string userId, string? nickname)
    {
        await nicknamesRepository.SetNicknameAsync(userId, nickname);
        _stateChanged.Invoke();
    }

    private async Task ReadPresenceAsync(CancellationToken ct)
    {
        using var reader = messagesChannel.Subscribe(m => m.Type == MessageType.Presence);
        await foreach (var message in reader.ReadAllAsync(ct))
        {
            var payload = message.SpecifyPayload<PresencePayload>();
            if (payload is null)
                continue;

            var nickname = nicknamesRepository.GetNickname(message.SenderClientId);

            var prev = _presences.TryGetValue(message.SenderClientId, out var existing)
                ? existing
                : null;

            _presences[message.SenderClientId] = new Contact(
                Id: message.SenderClientId,
                Name: payload.Payload.Name,
                LastSeen: DateTimeOffset.UtcNow,
                IsFavorite: _favorites.ContainsKey(message.SenderClientId),
                Nickname: nickname
            );

            PrunePresence();
            RebuildContacts();
            _stateChanged.Invoke();
        }
    }

    private bool PrunePresence()
    {
        var result = false;
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-30);
        foreach (var (key, _) in _presences.Where(u => u.Value.LastSeen < cutoff).ToArray())
        {
            result = true;
            _presences.TryRemove(key, out _);
        }

        return result;
    }

    private void RebuildContacts()
    {
        var merged = new Dictionary<string, Contact>(_presences.Count + _favorites.Count);

        foreach (var (id, contact) in _presences)
            merged[id] = contact with { IsFavorite = _favorites.ContainsKey(id) };

        foreach (var (id, name) in _favorites.Where(f => merged.ContainsKey(f.Key) is false))
        {
            var nickname = nicknamesRepository.GetNickname(id);
            merged[id] = new Contact(
                id,
                name,
                DateTimeOffset.MinValue,
                true,
                nickname
            );
        }

        Contacts =
        [
            .. merged
                .Values.OrderByDescending(v => v.IsFavorite)
                .ThenByDescending(v => v.Nickname is not null)
                .ThenBy(v => v.Nickname)
                .ThenBy(v => v.Name),
        ];
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
