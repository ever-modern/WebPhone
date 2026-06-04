using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using WebPhone.Backend.Actions;
using WebPhone.Backend.Storage;

namespace WebPhone.AzureEnd;

public sealed class SubscriptionFunction(SubscriptionApiAction action)
{
    static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    [Function("subscribe-for-push")]
    public async Task<IActionResult> Run([HttpTrigger(Microsoft.Azure.Functions.Worker.AuthorizationLevel.Anonymous, "post", "options")] HttpRequest req)
        => FunctionCors.BuildResult(await RunInternal(req), "POST, OPTIONS");

    public async Task<IActionResult> RunInternal([
        HttpTrigger(Microsoft.Azure.Functions.Worker.AuthorizationLevel.Anonymous, "post", "options")
    ] HttpRequest req)
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

        PushSubscriptionDto? subscription;
        try
        {
            subscription = await System.Text.Json.JsonSerializer.DeserializeAsync<PushSubscriptionDto>(req.Body, _jsonOptions, cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            return new BadRequestObjectResult("Invalid subscription payload");
        }
        if (subscription?.Endpoint is null or "")
        {
            return new BadRequestObjectResult("Missing required subscription fields");
        }
        var result = await action.ExecuteAsync(new SubscriptionActionInput(clientId, subscription), cancellationToken);
        return new OkObjectResult(new { success = result.Success });

    }
}
