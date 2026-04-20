namespace WebPhone.Contract;

public sealed record ContactSettingsDto(
    string OwnerId,
    string ContactId,
    bool IsFavourite,
    bool NotifyCalls,
    bool NotifyMessages,
    string? Nickname);
