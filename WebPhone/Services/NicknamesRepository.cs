using System.Collections.Concurrent;

namespace WebPhone.Services;

public sealed class NicknamesRepository(ILocalStore localStore)
{
    private const string StorageKey = "contact-nicknames";
    private readonly ConcurrentDictionary<string, string> _nicknames = [];
    private bool _loaded;

    public event Action? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded) return;
        var items = await localStore.GetAsync<List<ContactNickname>>(StorageKey, cancellationToken) ?? [];
        _nicknames.Clear();
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Id))
                _nicknames[item.Id] = item.Nickname;
        }
        _loaded = true;
    }

    public string? GetNickname(string userId)
        => _nicknames.TryGetValue(userId, out var n) ? n : null;

    public async Task SetNicknameAsync(string userId, string? nickname, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nickname))
            _nicknames.TryRemove(userId, out _);
        else
            _nicknames[userId] = nickname.Trim();

        await SaveAsync(cancellationToken);
        StateChanged?.Invoke();
    }

    private async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var items = _nicknames.Select(x => new ContactNickname(x.Key, x.Value)).ToList();
        await localStore.SetAsync(StorageKey, items, cancellationToken);
    }
}

public sealed record ContactNickname(string Id, string Nickname);
