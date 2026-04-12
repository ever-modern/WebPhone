using WebPhone.Services.Data;

namespace WebPhone.Services;

public record Contact(
    string Id,
    string Name,
    DateTimeOffset LastSeen,
    bool IsFavorite = false,
    string? Nickname = null
) : User(Id, Name);
