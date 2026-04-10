using EverModern.Events;

namespace WebPhone.Services;

public interface IProfile
{
    User User { get; }

    INotifier<User> UserChanged { get; }
}

public class ProfileStore(ILocalStore localStore) : IProfile
{
    private User? _user;

    public User User => _user ?? throw new InvalidOperationException("User is not initialized. Await InitializeAsync first.");

    readonly EventSource<User> _userChangedEvent = new();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_user is not null)
            return;

        var id = await localStore.GetAsync<string>("webrtc-user-id", cancellationToken);
        var name = await localStore.GetAsync<string>("webrtc-user-name", cancellationToken);

        _user = id is not null
            ? new User(id, name ?? "")
            : new User(Guid.NewGuid().ToString("N"), "");

        if (id is null)
            await SaveUserInfoAsync(_user, cancellationToken);
    }

    public void SetUser(User user)
    {
        _user = user;
        _userChangedEvent.Invoke(user);
        _ = SaveUserInfoAsync(user);
    }

    async Task SaveUserInfoAsync(User user, CancellationToken cancellationToken = default)
    {
        await localStore.SetAsync("webrtc-user-id", user.Id, cancellationToken);
        await localStore.SetAsync("webrtc-user-name", user.Name, cancellationToken);
    }

    public INotifier<User> UserChanged => _userChangedEvent;
}
