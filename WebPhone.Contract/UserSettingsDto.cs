namespace WebPhone.Contract;

public sealed record UserSettingsDto(
    string Name,
    bool NotifyCalls,
    bool NotifyMessages,
    bool NotifyFromEveryone);
