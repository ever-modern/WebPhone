using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebPhone.Backend.Services;
using WebPhone.Backend.Storage;
using WebPhone.Domain;

namespace WebPhone.Backend.Actions;

public sealed record SendChatInput(string ClientId, ChatSendRequest Request);

public sealed record GetChatMessagesInput(string ClientId, string PeerId, long? SinceId);

public sealed class SendChatApiAction(
    ILogger<SendChatApiAction> logger,
    MessagesWriter messagesWriter,
    PushNotifier pushNotificationService,
    ProfileSettingsRepository userSettingsRepository,
    ContactSettingsRepository contactSettingsRepository
) : ApiActionConcrete<SendChatInput, ChatMessageDto>
{
    private const string ChatMessageType = "UserChat";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public override string Route => "/chat/send";

    public override async Task<ChatMessageDto> ExecuteAsync(
        SendChatInput input,
        CancellationToken cancellationToken = default
    )
    {
        var payload = JsonSerializer.SerializeToElement(
            new { text = input.Request.Text },
            JsonOptions
        );
        var sentAt = DateTime.UtcNow;
        var messageId = CommonIdsGenerator.NewId();

        await messagesWriter.EnqueueAsync(
            [
                new MessageWriteEntry(
                    Type: ChatMessageType,
                    Payload: payload,
                    PublisherId: input.ClientId,
                    ReceiverId: input.Request.RecipientId,
                    DateTime: sentAt,
                    Id: messageId
                ),
            ],
            cancellationToken: cancellationToken
        );

        var dto = new ChatMessageDto(
            messageId,
            input.ClientId,
            input.Request.RecipientId,
            input.Request.Text,
            sentAt
        );

        var userSettings = await userSettingsRepository.GetAsync(
            input.Request.RecipientId,
            cancellationToken
        );
        var contactSettings = await contactSettingsRepository.GetAsync(
            input.Request.RecipientId,
            input.ClientId,
            cancellationToken
        );
        var shouldNotify =
            userSettings.NotifyMessages
            && (userSettings.NotifyFromEveryone || contactSettings.NotifyMessages);

        if (shouldNotify)
        {
            var pushPayload = JsonSerializer.Serialize(
                new
                {
                    type = "chat",
                    from = input.ClientId,
                    text = input.Request.Text,
                    sentAt = sentAt,
                }
            );

            _ = pushNotificationService.PushToClientAsync(
                input.Request.RecipientId,
                pushPayload,
                cancellationToken
            );
        }

        logger.LogInformation(
            "[CHAT] {From} → {To}: {Preview}, push={Push}",
            input.ClientId,
            input.Request.RecipientId,
            input.Request.Text[..Math.Min(input.Request.Text.Length, 60)],
            shouldNotify
        );

        return dto;
    }
}

public sealed class GetChatMessagesApiAction(MessagesReader repository)
    : ApiActionConcrete<GetChatMessagesInput, ChatMessageDto[]>
{
    public override string Route => "/chat/messages";

    public override async Task<ChatMessageDto[]> ExecuteAsync(
        GetChatMessagesInput input,
        CancellationToken cancellationToken = default
    )
    {
        var messages = await repository.ReadChatHistoryAsync(
            input.ClientId,
            input.PeerId,
            sinceId: input.SinceId,
            limit: 50,
            cancellationToken: cancellationToken
        );

        return [.. messages.Select(ToDto)];
    }

    private static ChatMessageDto ToDto(StoredMessage m)
    {
        var text = m.Payload.TryGetProperty("text", out var t)
            ? t.GetString() ?? string.Empty
            : string.Empty;
        return new ChatMessageDto(
            m.Id,
            m.PublisherId,
            m.ReceiverId ?? string.Empty,
            text,
            m.DateTime
        );
    }
}
