using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using System.Text.Json;
using WebPhone.Backend.Actions;
using WebPhone.Domain;

namespace WebPhone.AzureEnd;

public sealed class NotifyFunction(NotifyApiAction action)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    [Function("notify")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", "options")] HttpRequest req)
    {
        if (FunctionCors.TryBuildPreflightResult(req, "POST, OPTIONS") is { } preflightResult)
        {
            return preflightResult;
        }

        var cancellationToken = req.HttpContext.RequestAborted;

        var senderClientId = req.Headers["X-Client-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(senderClientId))
        {
            return new BadRequestObjectResult("Missing X-Client-Id header");
        }

        NotifyRequest? notifyRequest;
        try
        {
            notifyRequest = await JsonSerializer.DeserializeAsync<NotifyRequest>(req.Body, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return new BadRequestObjectResult("Invalid notify payload");
        }

        var result = await action.ExecuteAsync(new NotifyActionInput(senderClientId, notifyRequest), cancellationToken);

        return FunctionCors.BuildResult(new OkObjectResult(new { success = result.Success, targetClientId = result.TargetClientId }), "POST, OPTIONS");
    }
}
