namespace WebPhone.Services;

public class Profile(ILocalStore LocalStore)
{
    public async Task<User?> GetUserInfoAsync(CancellationToken cancellationToken = default)
    {
        var id = await LocalStore.GetAsync<string>("webrtc-user-id", cancellationToken);
        var name = await LocalStore.GetAsync<string>("webrtc-user-name", cancellationToken);

        if (id is null)
        {
            return null;
        }

        var result = new User(id, name ?? "");
        return result;
    }

    public async Task SaveUserInfoAsync(User user, CancellationToken cancellationToken = default)
    {
        await LocalStore.SetAsync("webrtc-user-id", user.Id, cancellationToken);
        await LocalStore.SetAsync("webrtc-user-name", user.Name, cancellationToken);
    }
}
