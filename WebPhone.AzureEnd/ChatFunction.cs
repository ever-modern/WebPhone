using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using WebPhone.AzureEnd.Services;
using WebPhone.AzureEnd.Storage;
using WebPhone.Contract;

namespace WebPhone.AzureEnd;

public sealed class ChatFunction(
    ILogger<ChatFunction> logger,
    MessagesRepository repository,
    PushNotificationService pushNotificationService,
    ProfileSettingsRepository userSettingsRepository,
    ContactSettingsRepository contactSettingsRepository)
{
    private const string ChatMessageType = "UserChat";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── POST /api/chat/send ────────────────────────────────────────────────
    // Body: { text: string, recipientId: string }
    // Header: X-Client-Id
    // Returns: ChatMessageDto of the persisted message.
    [Function("chat-send")]
    public async Task<IActionResult> Send(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options", Route = "chat/send")] HttpRequest req)
    {
        if (FunctionCors.TryBuildPreflightResult(req, "POST, OPTIONS") is { } preflight)
            return preflight;

        var clientId = req.Headers["X-Client-Id"].FirstOrDefault();
        if (string.IsNullOrEmpty(clientId))
            return FunctionCors.BuildResult(new BadRequestObjectResult("Missing X-Client-Id header"), "POST, OPTIONS");

        ChatSendRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ChatSendRequest>(
                req.Body, JsonOptions, req.HttpContext.RequestAborted);
        }
        catch
        {
            return FunctionCors.BuildResult(new BadRequestObjectResult("Invalid JSON body"), "POST, OPTIONS");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Text) || string.IsNullOrWhiteSpace(request.RecipientId))
            return FunctionCors.BuildResult(new BadRequestObjectResult("text and recipientId are required"), "POST, OPTIONS");

        var payload = JsonSerializer.SerializeToElement(new { text = request.Text }, JsonOptions);
        var sentAt = DateTime.UtcNow;
        var messageId = CommonIdsGenerator.NewId();

        // Write through the existing repository infrastructure.
        await repository.WriteMessagesAsync(
        [
            new MessageWriteEntry(ChatMessageType, payload, clientId, request.RecipientId, sentAt, messageId)
        ], cancellationToken: req.HttpContext.RequestAborted);

        // We generated the message ID, so we can return a deterministic DTO immediately.
        var dto = new ChatMessageDto(messageId, clientId, request.RecipientId, request.Text, sentAt);

        // Push notification eligibility
        // 1) recipient global settings must allow message notifications
        // 2) if recipient does not allow everyone, sender must be explicitly allowed on contact settings
        // 3) contact-level notify_messages must be true
        var userSettings = await userSettingsRepository.GetAsync(request.RecipientId, req.HttpContext.RequestAborted);
        var contactSettings = await contactSettingsRepository.GetAsync(request.RecipientId, clientId, req.HttpContext.RequestAborted);
        var shouldNotify = userSettings.NotifyMessages
            && (userSettings.NotifyFromEveryone || contactSettings.NotifyMessages);

        if (shouldNotify)
        {
            var pushPayload = JsonSerializer.Serialize(new
            {
                type = "chat",
                from = clientId,
                text = request.Text,
                sentAt = sentAt
            });

            _ = pushNotificationService.PushToClientAsync(request.RecipientId, pushPayload, req.HttpContext.RequestAborted);
        }

        logger.LogInformation("[CHAT] {From} → {To}: {Preview}, push={Push}",
            clientId, request.RecipientId, request.Text[..Math.Min(request.Text.Length, 60)], shouldNotify);


        return FunctionCors.BuildResult(new OkObjectResult(dto), "POST, OPTIONS");
    }

    // ── GET /api/chat/messages ─────────────────────────────────────────────
    // Query: peerId (required), sinceId (optional, 0 = load latest 50)
    // Header: X-Client-Id
    // Returns: ChatMessageDto[]
    [Function("chat-messages")]
    public async Task<IActionResult> GetMessages(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options", Route = "chat/messages")] HttpRequest req)
    {
        if (FunctionCors.TryBuildPreflightResult(req, "GET, OPTIONS") is { } preflight)
            return preflight;

        var clientId = req.Headers["X-Client-Id"].FirstOrDefault();
        if (string.IsNullOrEmpty(clientId))
            return FunctionCors.BuildResult(new BadRequestObjectResult("Missing X-Client-Id header"), "GET, OPTIONS");

        var peerId = req.Query["peerId"].FirstOrDefault();
        if (string.IsNullOrEmpty(peerId))
            return FunctionCors.BuildResult(new BadRequestObjectResult("peerId query param is required"), "GET, OPTIONS");

        long? sinceId = long.TryParse(req.Query["sinceId"].FirstOrDefault(), out var parsed) && parsed > 0
            ? parsed
            : null;

        var messages = await repository.ReadChatHistoryAsync(
            clientId, peerId,
            sinceId: sinceId,
            limit: 50,
            cancellationToken: req.HttpContext.RequestAborted);

        var dtos = messages.Select(ToDto).ToArray();

        return FunctionCors.BuildResult(new OkObjectResult(dtos), "GET, OPTIONS");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static ChatMessageDto ToDto(StoredMessage m)
    {
        var text = m.Payload.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        return new ChatMessageDto(m.Id, m.PublisherId, m.ReceiverId ?? string.Empty, text, m.DateTime);
    }
}
