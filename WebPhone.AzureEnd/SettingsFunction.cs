using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using WebPhone.AzureEnd.Storage;
using WebPhone.Contract;

namespace WebPhone.AzureEnd;

public sealed class SettingsFunction(
    ProfileSettingsRepository userSettingsRepository,
    ContactSettingsRepository contactSettingsRepository)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    [Function("settings-profile")]
    public async Task<IActionResult> UserSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "options", Route = "profiles")] HttpRequest req)
    {
        if (FunctionCors.TryBuildPreflightResult(req, "GET, POST, OPTIONS") is { } preflight)
            return preflight;

        var ownerId = req.Headers["X-Client-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ownerId))
            return FunctionCors.BuildResult(new BadRequestObjectResult("Missing X-Client-Id header"), "GET, POST, OPTIONS");

        if (HttpMethods.IsGet(req.Method))
        {
            var result = await userSettingsRepository.GetAsync(ownerId, req.HttpContext.RequestAborted);
            return FunctionCors.BuildResult(new OkObjectResult(result), "GET, POST, OPTIONS");
        }

        if (!HttpMethods.IsPost(req.Method))
            return FunctionCors.BuildResult(new StatusCodeResult(StatusCodes.Status405MethodNotAllowed), "GET, POST, OPTIONS");

        UserSettingsDto? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<UserSettingsDto>(req.Body, JsonOptions, req.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return FunctionCors.BuildResult(new BadRequestObjectResult("Invalid JSON body"), "GET, POST, OPTIONS");
        }

        if (body is null)
            return FunctionCors.BuildResult(new BadRequestObjectResult("Missing request body"), "GET, POST, OPTIONS");

        await userSettingsRepository.UpsertAsync(ownerId, body, req.HttpContext.RequestAborted);
        return FunctionCors.BuildResult(new OkObjectResult(new { success = true }), "GET, POST, OPTIONS");
    }

    [Function("settings-contact")]
    public async Task<IActionResult> ContactSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "options", Route = "contacts")] HttpRequest req)
    {
        if (FunctionCors.TryBuildPreflightResult(req, "GET, POST, OPTIONS") is { } preflight)
            return preflight;

        var ownerId = req.Headers["X-Client-Id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ownerId))
            return FunctionCors.BuildResult(new BadRequestObjectResult("Missing X-Client-Id header"), "GET, POST, OPTIONS");

        if (HttpMethods.IsGet(req.Method))
        {
            var contactId = req.Query["contactId"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(contactId))
            {
                var all = await contactSettingsRepository.GetByOwnerAsync(ownerId, req.HttpContext.RequestAborted);
                return FunctionCors.BuildResult(new OkObjectResult(all), "GET, POST, OPTIONS");
            }

            var result = await contactSettingsRepository.GetAsync(ownerId, contactId, req.HttpContext.RequestAborted);
            return FunctionCors.BuildResult(new OkObjectResult(result), "GET, POST, OPTIONS");
        }

        if (!HttpMethods.IsPost(req.Method))
            return FunctionCors.BuildResult(new StatusCodeResult(StatusCodes.Status405MethodNotAllowed), "GET, POST, OPTIONS");

        ContactSettingsDto? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<ContactSettingsDto>(req.Body, JsonOptions, req.HttpContext.RequestAborted);
        }
        catch (JsonException)
        {
            return FunctionCors.BuildResult(new BadRequestObjectResult("Invalid JSON body"), "GET, POST, OPTIONS");
        }

        if (body is null || string.IsNullOrWhiteSpace(body.ContactId))
            return FunctionCors.BuildResult(new BadRequestObjectResult("contactId is required"), "GET, POST, OPTIONS");

        // Owner is always authenticated via X-Client-Id; ignore spoofed owner in payload.
        var normalized = body with { OwnerId = ownerId };
        await contactSettingsRepository.UpsertAsync(normalized, req.HttpContext.RequestAborted);

        return FunctionCors.BuildResult(new OkObjectResult(new { success = true }), "GET, POST, OPTIONS");
    }
}
