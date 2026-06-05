using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using WebPhone.Backend.Actions;

namespace WebPhone.AzureEnd;

public sealed class HealthFunction(HealthCheckApiAction action)
{
    [Function("health")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options", Route = "health")] HttpRequest req)
    {
        if (FunctionCors.TryBuildPreflightResult(req, "GET, OPTIONS") is { } preflight)
            return preflight;

        var result = await action.ExecuteAsync(null, req.HttpContext.RequestAborted);

        return result.Healthy
            ? FunctionCors.BuildResult(new OkObjectResult(result), "GET, OPTIONS")
            : FunctionCors.BuildResult(
                new ObjectResult(result) { StatusCode = StatusCodes.Status503ServiceUnavailable },
                "GET, OPTIONS");
    }
}
