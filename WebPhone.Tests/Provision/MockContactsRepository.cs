using EverModern.Events;
using WebPhone.Data;

namespace WebPhone.Tests.Provision;

public class MockContactsRepository(string selfId, IValueNotifier<IReadOnlyList<Contact>> contacts)
    : IContactsRepository
{
    public IReadOnlyList<Contact> Contacts => [.. contacts.Value.Where(v=>v.Id != selfId)];

    public INotifier StateChanged => contacts;

    public Task ToggleFavoriteAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SetNicknameAsync(string userId, string? nickname) => Task.CompletedTask;
}
