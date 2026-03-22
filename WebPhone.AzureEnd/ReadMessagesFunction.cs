using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using WebPhone.AzureEnd.Storage;
using WebPhone.Registration;

namespace WebPhone.AzureEnd;

public sealed class ReadMessagesFunction(ILogger<ReadMessagesFunction> logger, MessagesRepository repository)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Function("read-messages")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "options")] HttpRequest req)
    {
        if (FunctionCors.TryBuildPreflightResult(req, "GET, OPTIONS") is { } preflightResult)
        {
            return preflightResult;
        }

        var cancellationToken = req.HttpContext.RequestAborted;
        DateTimeOffset? since = null;

        if (req.Query.TryGetValue("since", out var sinceValues) && !string.IsNullOrWhiteSpace(sinceValues))
        {
            if (!DateTimeOffset.TryParse(sinceValues.ToString(), out var parsedSince))
            {
                logger.LogWarning("Read messages failed: invalid since parameter {Since}.", sinceValues.ToString());
                return FunctionCors.BuildResult(new BadRequestObjectResult("Invalid since parameter."), "GET, OPTIONS");
            }

            since = parsedSince;
        }

        var messages = await repository.ReadMessagesAsync(since, cancellationToken);
        var response = messages
            .Select(message => new MessageEnvelope(message.DateTime, message.Type, message.Payload))
            .ToArray();

        logger.LogInformation("Read messages returned {MessageCount} entries.", response.Length);
        return FunctionCors.BuildResult(new OkObjectResult(response), "GET, OPTIONS");
    }

    private sealed record MessageEnvelope(DateTimeOffset DateTime, MessageType Type, JsonElement Payload);
}
