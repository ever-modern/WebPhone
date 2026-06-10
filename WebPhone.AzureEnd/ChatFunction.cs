using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using WebPhone.Backend.Actions;
using WebPhone.Domain;

namespace WebPhone.AzureEnd;

public sealed class ChatFunction(
    SendChatApiAction sendChatAction,
    GetChatMessagesApiAction getChatMessagesAction)
{
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

        var dto = await sendChatAction.ExecuteAsync(
            new SendChatInput(clientId, request),
            req.HttpContext.RequestAborted);


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

        var dtos = await getChatMessagesAction.ExecuteAsync(
            new GetChatMessagesInput(clientId, peerId, sinceId),
            req.HttpContext.RequestAborted);

        return FunctionCors.BuildResult(new OkObjectResult(dtos), "GET, OPTIONS");
    }
}
