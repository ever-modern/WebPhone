using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using System.Text.Json;
using WebPhone.AzureEnd.Services;
using WebPhone.Contract;

namespace WebPhone.AzureEnd;

public sealed class NotifyFunction(PushNotificationService pushNotificationService)
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

        var targetClientId = string.IsNullOrWhiteSpace(notifyRequest?.TargetClientId)
            ? senderClientId
            : notifyRequest.TargetClientId;

        var message = string.IsNullOrWhiteSpace(notifyRequest?.Message)
            ? $"Notification from {senderClientId}."
            : notifyRequest.Message;

        var sent = await pushNotificationService.PushToClientAsync(targetClientId!, message, cancellationToken);

        return FunctionCors.BuildResult(new OkObjectResult(new { success = sent, targetClientId }), "POST, OPTIONS");
    }
}
