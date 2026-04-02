namespace WebPhone.Services;

public interface IProfile
{
    public User User { get; }

    public event Action<User>? UserChanged;
}

public class ProfileStore(ILocalStore localStore) : IProfile
{
    public User User { get; private set; } = new(Guid.NewGuid().ToString("N"), "");

    public void SetUser(User user)
    {
        User = user;
        UserChanged?.Invoke(user);
        _ = SaveUserInfoAsync(user);
    }

    public async Task<User?> GetUserFromStoreAsync(CancellationToken cancellationToken = default)
    {
        var id = await localStore.GetAsync<string>("webrtc-user-id", cancellationToken);
        var name = await localStore.GetAsync<string>("webrtc-user-name", cancellationToken);

        if (id is null)
        {
            return null;
        }

        var result = new User(id, name ?? "");
        return result;
    }

    async Task SaveUserInfoAsync(User user, CancellationToken cancellationToken = default)
    {
        await localStore.SetAsync("webrtc-user-id", user.Id, cancellationToken);
        await localStore.SetAsync("webrtc-user-name", user.Name, cancellationToken);
    }

    public event Action<User>? UserChanged;
}


