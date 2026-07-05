using EverModern.Events;
using WebPhone.Data;

namespace WebPhone.Tests.Provision;

public class MockContactsRepository(string selfId, IValueNotifier<IReadOnlyList<Contact>> contacts)
    : IContactsRepository
{
    static ObservedValue<T> Process<T>(IValueNotifier<T> input, Func<T, T> transformer)
    {
        var observed = new ObservedValue<T>(input.Value);
        input.Subscribe(v => observed.Change(transformer(v)));
        return observed;
    }

    public IValueNotifier<IReadOnlyList<Contact>> Contacts { get; } =
        Process(contacts, c => [.. c.Where(cc => cc.Id != selfId)]);

    public Task ToggleFavoriteAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SetNicknameAsync(string userId, string? nickname) => Task.CompletedTask;
}
