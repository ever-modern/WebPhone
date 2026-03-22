using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using WebPhone.AzureEnd.Storage;
using WebPhone.Registration;

namespace WebPhone.AzureEnd;

[Obsolete("Use exchange function instead.")]
public class AnnouncePresenceFunction(ILogger<AnnouncePresenceFunction> logger, MessagesRepository repository)
{
    private static readonly TimeSpan PresenceCutoff = TimeSpan.FromSeconds(7);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Function("announce-presence")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", "options")] HttpRequest req)
    {
        if (FunctionCors.TryBuildPreflightResult(req, "POST, OPTIONS") is { } preflightResult)
        {
            return preflightResult;
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
            return FunctionCors.BuildResult(new BadRequestObjectResult("Invalid JSON."), "POST, OPTIONS");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.Name))
        {
            logger.LogWarning("Presence announce failed: missing fields.");
            return FunctionCors.BuildResult(new BadRequestObjectResult("Missing required fields."), "POST, OPTIONS");
        }

        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            var payload = JsonSerializer.SerializeToElement(new PresenceAnnounceRequest(request.UserId, request.Name, timestamp), JsonOptions);
            await repository.WriteMessageAsync(MessageTypeJsonConverter.ToWireValue(MessageType.Presence), payload, cancellationToken: cancellationToken);
            logger.LogInformation("Presence stored for user {UserId} at {Timestamp}.", request.UserId, timestamp);

            var cutoff = DateTimeOffset.UtcNow - PresenceCutoff;
            var messages = await repository.ReadMessagesAsync(cutoff, cancellationToken);

            var presentUsers = messages.Where(m => string.Equals(m.Type, MessageTypeJsonConverter.ToWireValue(MessageType.Presence), StringComparison.OrdinalIgnoreCase))
                .Select(TryDeserializePresence)
                .Where(m => m is not null)
                .Select(m => m!)
                .OrderByDescending(m => m.Timestamp)
                .DistinctBy(m => m.UserId)
                .Where(m => m.UserId != request.UserId)
                .Select(m => new PresenceAnnounceResponse(m.UserId, m.Name, m.Timestamp))
                .ToArray();

            logger.LogInformation("Presence announce response contains {UserCount} users.", presentUsers.Length);
            return FunctionCors.BuildResult(new OkObjectResult(presentUsers), "POST, OPTIONS");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Presence announce failed.");
            return FunctionCors.BuildResult(new ObjectResult("Presence announce failed.")
            {
                StatusCode = StatusCodes.Status500InternalServerError,
            }, "POST, OPTIONS");
        }
    }

    private sealed record PresenceAnnounceRequest(string UserId, string Name, DateTimeOffset Timestamp);

    private sealed record PresenceAnnounceResponse(string UserId, string Name, DateTimeOffset Timestamp);

    private PresenceAnnounceRequest? TryDeserializePresence(StoredMessage message)
    {
        try
        {
            return JsonSerializer.Deserialize<PresenceAnnounceRequest>(message.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Skipping invalid presence payload from {Timestamp}.", message.DateTime);
            return null;
        }
    }

}