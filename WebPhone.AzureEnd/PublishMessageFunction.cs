using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using WebPhone.AzureEnd.Storage;
using WebPhone.Registration;

namespace WebPhone.AzureEnd;

public sealed class PublishMessageFunction(ILogger<PublishMessageFunction> logger, MessagesRepository repository)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Function("publish-message")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", "options")] HttpRequest req)
    {
        if (FunctionCors.TryBuildPreflightResult(req, "POST, OPTIONS") is { } preflightResult)
        {
            return preflightResult;
        }

        var cancellationToken = req.HttpContext.RequestAborted;
        Message? message;

        try
        {
            message = await JsonSerializer.DeserializeAsync<Message>(req.Body, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            logger.LogWarning("Publish message failed: invalid JSON.");
            return FunctionCors.BuildResult(new BadRequestObjectResult("Invalid JSON."), "POST, OPTIONS");
        }

        if (message is null || message.Type == MessageType.Unknown)
        {
            logger.LogWarning("Publish message failed: missing or invalid fields.");
            return FunctionCors.BuildResult(new BadRequestObjectResult("Missing required fields."), "POST, OPTIONS");
        }

        await repository.WriteMessageAsync(message.Type, message.Payload, cancellationToken);
        logger.LogInformation("Stored message of type {MessageType}.", message.Type);
        return FunctionCors.BuildResult(new OkResult(), "POST, OPTIONS");
    }
}
