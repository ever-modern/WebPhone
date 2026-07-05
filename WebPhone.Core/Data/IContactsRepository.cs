using EverModern.Events;

namespace WebPhone.Data;

public interface IContactsRepository
{
    IValueNotifier<IReadOnlyList<Contact>> Contacts { get; }

    Task ToggleFavoriteAsync(string userId, CancellationToken cancellationToken = default);
    Task SetNicknameAsync(string userId, string? nickname);
}
