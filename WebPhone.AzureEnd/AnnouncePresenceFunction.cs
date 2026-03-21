using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using WebPhone.AzureEnd.Storage;
using WebPhone.Registration;

namespace WebPhone.AzureEnd;

public class AnnouncePresenceFunction(ILogger<AnnouncePresenceFunction> logger, MessagesRepository repository)
{
    private static readonly TimeSpan PresenceCutoff = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Function("announce-presence")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", "options")] HttpRequest req)
    {
        if (HttpMethods.IsOptions(req.Method))
        {
            return BuildCorsResult(new OkResult());
        }

        logger.LogInformation("Presence announce request received.");
        var cancellationToken = req.HttpContext.RequestAborted;
        PresenceAnnounceRequest? request;

        try
        {
            request = await JsonSerializer.DeserializeAsync<PresenceAnnounceRequest>(req.Body, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            logger.LogWarning("Presence announce failed: invalid JSON.");
            return BuildCorsResult(new BadRequestObjectResult("Invalid JSON."));
        }

        if (request is null || string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.Name))
        {
            logger.LogWarning("Presence announce failed: missing fields.");
            return BuildCorsResult(new BadRequestObjectResult("Missing required fields."));
        }

        var timestamp = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.SerializeToElement(new PresenceAnnounceRequest(request.UserId, request.Name, timestamp), JsonOptions);
        await repository.WriteMessageAsync(MessageType.Presence, payload, cancellationToken);
        logger.LogInformation("Presence stored for user {UserId} at {Timestamp}.", request.UserId, timestamp);

        var cutoff = DateTimeOffset.UtcNow - PresenceCutoff;
        var messages = await repository.ReadMessagesAsync(cutoff, cancellationToken);

        var presentUsers = messages.Where(m => m.Type == MessageType.Presence)
            .Select(m => JsonSerializer.Deserialize<PresenceAnnounceRequest>(m.Payload, JsonOptions))
            .DistinctBy(m => m.UserId)
            .Where(m => m.UserId != request.UserId)
            .ToArray();

        logger.LogInformation("Presence announce response contains {UserCount} users.", presentUsers.Length);
        return BuildCorsResult(new OkObjectResult(presentUsers));
    }

    private sealed record PresenceAnnounceRequest(string UserId, string Name, DateTimeOffset Timestamp);

    private sealed record PresenceAnnounceResponse(string UserId, string Name, DateTimeOffset Timestamp);

    private static IActionResult BuildCorsResult(IActionResult result)
        => new CorsResult(result);

    private sealed class CorsResult(IActionResult inner) : IActionResult
    {
        public async Task ExecuteResultAsync(ActionContext context)
        {
            var headers = context.HttpContext.Response.Headers;
            headers["Access-Control-Allow-Origin"] = "*";
            headers["Access-Control-Allow-Headers"] = "Content-Type";
            headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
            await inner.ExecuteResultAsync(context);
        }
    }
}