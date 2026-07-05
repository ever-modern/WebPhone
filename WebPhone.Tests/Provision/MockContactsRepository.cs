using EverModern.Events;
using WebPhone.Data;
using WebPhone.Domain;

namespace WebPhone.Tests.Provision;

public class MockContactsRepository(string selfId, IValueNotifier<IReadOnlyList<Contact>> contacts)
    : IContactsRepository
{

    public IValueNotifier<IReadOnlyList<Contact>> Contacts { get; } =
        contacts.Transform(c => [.. c.Where(cc => cc.Id != selfId)]);

    public Task ToggleFavoriteAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SetNicknameAsync(string userId, string? nickname) => Task.CompletedTask;
}
