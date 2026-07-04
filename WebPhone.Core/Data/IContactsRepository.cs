using EverModern.Events;

namespace WebPhone.Data;

public interface IContactsRepository
{
    IReadOnlyList<Contact> Contacts { get; }
    INotifier StateChanged { get; }
    Task ToggleFavoriteAsync(string userId, CancellationToken cancellationToken = default);
    Task SetNicknameAsync(string userId, string? nickname);
}
