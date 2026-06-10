using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using WebPhone.Backend.Actions;
using WebPhone.Domain;

namespace WebPhone.AzureEnd;

public sealed class ExchangeFunction(
    ILogger<ExchangeFunction> logger,
    ExchangeApiAction action
)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Function("exchange")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options")] HttpRequest req
    )
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

        var request = await JsonSerializer.DeserializeAsync<ExchangeRequest>(
            req.Body,
            JsonOptions,
            cancellationToken
        );

        if (request is null)
        {
            return FunctionCors.BuildResult(new BadRequestObjectResult("Missing request body"), "POST, OPTIONS");
        }

        var response = await action.ExecuteAsync(new ExchangeActionInput(clientId, request), cancellationToken);

        return FunctionCors.BuildResult(new ObjectResult(response), "POST, OPTIONS");
    }
}
