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
        if (HttpMethods.IsOptions(req.Method))
        {
            return BuildCorsResult(new OkResult());
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
            return BuildCorsResult(new BadRequestObjectResult("Invalid JSON."));
        }

        if (message is null || message.Type == MessageType.Unknown)
        {
            logger.LogWarning("Publish message failed: missing or invalid fields.");
            return BuildCorsResult(new BadRequestObjectResult("Missing required fields."));
        }

        await repository.WriteMessageAsync(message.Type, message.Payload, cancellationToken);
        logger.LogInformation("Stored message of type {MessageType}.", message.Type);
        return BuildCorsResult(new OkResult());
    }

    private static IActionResult BuildCorsResult(IActionResult result)
        => new CorsResult(result);

    private sealed class CorsResult(IActionResult inner) : IActionResult
    {
        public async Task ExecuteResultAsync(ActionContext context)
        {
            var headers = context.HttpContext.Response.Headers;
            headers["Access-Control-Allow-Origin"] = "*";
            headers["Access-Control-Allow-Headers"] = "Content-Type";
            headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
            await inner.ExecuteResultAsync(context);
        }
    }
}
