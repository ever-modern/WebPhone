using System.Collections.Concurrent;
using EverModern.Events;
using WebPhone.Channels;
using WebPhone.Domain;
using WebPhone.Messages;

namespace WebPhone.Data;

public class ContactsRepository(
    IMessagesChannel messagesChannel,
    IBackendClient backendClient,
    IProfile profile
) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Contact> _presences = [];
    private readonly ConcurrentDictionary<string, ContactSettingsDto> _contactSettings = [];
    private CancellationTokenSource? _cts;
    private Task? _readerTask;

    public IReadOnlyList<Contact> Contacts { get; private set; } = [];

    readonly EventSource _stateChanged = new();
    public INotifier StateChanged => _stateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var all = await backendClient.GetAllContactSettingsAsync(cancellationToken);
        _contactSettings.Clear();
        foreach (var s in all)
        {
            if (!string.IsNullOrWhiteSpace(s.ContactId))
                _contactSettings[s.ContactId] = s;
        }

        // Ensure user settings exist server-side (at least defaults) and keep name in sync.
        var user = await backendClient.GetUserSettingsAsync(cancellationToken);
        if (!string.Equals(user.Name, profile.User.Name, StringComparison.Ordinal))
            await backendClient.UpsertUserSettingsAsync(
                user with
                {
                    Name = profile.User.Name,
                },
                cancellationToken
            );

        RebuildContacts();
    }

    public void StartTracking()
    {
        _cts = new CancellationTokenSource();
        _readerTask = ReadPresenceAsync(_cts.Token);
    }

    public async Task ToggleFavoriteAsync(
        string userId,
        CancellationToken cancellationToken = default
    )
    {
        var current = _contactSettings.TryGetValue(userId, out var existing)
            ? existing
            : new ContactSettingsDto(profile.User.Id, userId, false, true, true, null);

        var name = _presences.TryGetValue(userId, out var presence) ? presence.Name : userId;

        var updated = current with
        {
            IsFavourite = !current.IsFavourite,
            // Keep a friendly display fallback so offline favourites don't render raw IDs.
            Nickname = string.IsNullOrWhiteSpace(current.Nickname) ? name : current.Nickname,
        };
        _contactSettings[userId] = updated;
        await backendClient.UpsertContactSettingsAsync(updated, cancellationToken);
        RebuildContacts();
        _stateChanged.Invoke();
    }

    public async Task SetNicknameAsync(string userId, string? nickname)
    {
        var current = _contactSettings.TryGetValue(userId, out var existing)
            ? existing
            : new ContactSettingsDto(profile.User.Id, userId, false, true, true, null);

        var updated = current with
        {
            Nickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim(),
        };
        _contactSettings[userId] = updated;
        await backendClient.UpsertContactSettingsAsync(updated);
        RebuildContacts();
        _stateChanged.Invoke();
    }

    private async Task ReadPresenceAsync(CancellationToken ct)
    {
        using var reader = messagesChannel.Subscribe(m => m.Type == MessageType.Presence);
        await foreach (var message in reader.ReadAllAsync(ct))
        {
            var payload = message.SpecifyPayload<PresencePayload>();
            if (payload is null || message.SenderClientId is null)
                continue;

            _contactSettings.TryGetValue(message.SenderClientId, out var settings);
            var nickname = settings?.Nickname;

            var prev = _presences.TryGetValue(message.SenderClientId, out var existing)
                ? existing
                : null;

            _presences[message.SenderClientId] = new Contact(
                Id: message.SenderClientId,
                Name: payload.Payload.Name,
                LastSeen: DateTimeOffset.UtcNow,
                IsFavorite: settings?.IsFavourite ?? false,
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
        var merged = new Dictionary<string, Contact>(_presences.Count + _contactSettings.Count);

        foreach (var (id, contact) in _presences)
        {
            _contactSettings.TryGetValue(id, out var setting);
            merged[id] = contact with
            {
                IsFavorite = setting?.IsFavourite ?? false,
                Nickname = setting?.Nickname,
            };
        }

        foreach (
            var (id, setting) in _contactSettings.Where(f => merged.ContainsKey(f.Key) is false)
        )
        {
            if (!setting.IsFavourite && string.IsNullOrWhiteSpace(setting.Nickname))
                continue;

            merged[id] = new Contact(
                id,
                setting.Nickname ?? "Contact",
                DateTimeOffset.MinValue,
                setting.IsFavourite,
                setting.Nickname
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

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_readerTask is not null)
            await _readerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _cts?.Dispose();
    }
}
