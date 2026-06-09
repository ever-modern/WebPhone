using WebPhone.Contract;

namespace WebPhone.Services;

public interface IBackendClient
{
    Task<RtcMatchParameter> ConnectRtcAsync(RtcConnectionRequest connectionRequest, CancellationToken cancellationToken);
    Task<ExchangeResponse> ExchangeAsync(MessageRequest[] outgoingMessages, long messagesSinceId, CancellationToken cancellationToken = default);
    Task<ContactSettingsDto[]> GetAllContactSettingsAsync(CancellationToken cancellationToken = default);
    Task<ChatMessageDto[]> GetChatMessagesAsync(string peerId, long sinceId = 0, CancellationToken cancellationToken = default);
    Task<ContactSettingsDto> GetContactSettingsAsync(string contactId, CancellationToken cancellationToken = default);
    Task<UserSettingsDto> GetUserSettingsAsync(CancellationToken cancellationToken = default);
    Task NotifyAsync(string? targetClientId, string? message, CancellationToken cancellationToken = default);
    Task RegisterPushSubscriptionAsync(string subscriptionPayload, CancellationToken cancellationToken = default);
    Task<ChatMessageDto> SendChatMessageAsync(string recipientId, string text, CancellationToken cancellationToken = default);
    Task UpsertContactSettingsAsync(ContactSettingsDto dto, CancellationToken cancellationToken = default);
    Task UpsertUserSettingsAsync(UserSettingsDto dto, CancellationToken cancellationToken = default);
}