using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using WebPhone.AzureEnd.Storage;
using WebPhone.Contract;
using WebPhone.Registration;

namespace WebPhone.AzureEnd;

public sealed record ExchangeFunction(ILogger<ExchangeFunction> logger, MessagesRepository repository)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Function("exchange")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", "options")] HttpRequest req)
    {
        if (FunctionCors.TryBuildPreflightResult(req, "POST, OPTIONS") is { } preflightResult)
        {
            return preflightResult;
        }

        var cancellationToken = req.HttpContext.RequestAborted;

        var clientId = req.Headers["X-Client-Id"].FirstOrDefault();

        if (clientId is null or "")
        {
            return new BadRequestObjectResult("Missing X-Client-Id header");
        }

        var request = await JsonSerializer.DeserializeAsync<ExchangeRequest>(req.Body, JsonOptions, cancellationToken);

        var now = DateTime.UtcNow;

        await repository.WriteMessagesAsync(
            [.. request.Messages?.Select(
                    m => new MessageWriteEntry(m.Type, m.Payload, clientId, m.TargetClientId, now)) ?? [],
                new MessageWriteEntry("Presence", null, clientId, null, now)
            ]);

        var relevantMessages = await repository.ReadMessagesAsync(new MessagesFilter(
            ReceiverId: clientId, Since: request.MessagesActualityCutoffDate), cancellationToken);

        var response = new ExchangeResponse(
            [.. relevantMessages.Select(m => new MessageResponse(m.PublisherId, m.Type, m.DateTime, m.Payload))]);

        return FunctionCors.BuildResult(new ObjectResult(response), "POST, OPTIONS");
    }

}
