using EverModern.Events;
using WebPhone.Services.Data;

namespace WebPhone.Tests.Provision;

public class TestProfile(string userId) : IProfile
{
    public User User { get; } = new User(userId, userId);

    public INotifier<User> UserChanged => throw new NotImplementedException();
}
