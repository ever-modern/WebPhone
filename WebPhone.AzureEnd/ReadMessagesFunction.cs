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
        if (HttpMethods.IsOptions(req.Method))
        {
            return BuildCorsResult(new OkResult());
        }

        var cancellationToken = req.HttpContext.RequestAborted;
        DateTimeOffset? since = null;

        if (req.Query.TryGetValue("since", out var sinceValues) && !string.IsNullOrWhiteSpace(sinceValues))
        {
            if (!DateTimeOffset.TryParse(sinceValues.ToString(), out var parsedSince))
            {
                logger.LogWarning("Read messages failed: invalid since parameter {Since}.", sinceValues.ToString());
                return BuildCorsResult(new BadRequestObjectResult("Invalid since parameter."));
            }

            since = parsedSince;
        }

        var messages = await repository.ReadMessagesAsync(since, cancellationToken);
        var response = messages
            .Select(message => new MessageEnvelope(message.DateTime, message.Type, message.Payload))
            .ToArray();

        logger.LogInformation("Read messages returned {MessageCount} entries.", response.Length);
        return BuildCorsResult(new OkObjectResult(response));
    }

    private sealed record MessageEnvelope(DateTimeOffset DateTime, MessageType Type, JsonElement Payload);

    private static IActionResult BuildCorsResult(IActionResult result)
        => new CorsResult(result);

    private sealed class CorsResult(IActionResult inner) : IActionResult
    {
        public async Task ExecuteResultAsync(ActionContext context)
        {
            var headers = context.HttpContext.Response.Headers;
            headers["Access-Control-Allow-Origin"] = "*";
            headers["Access-Control-Allow-Headers"] = "Content-Type";
            headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
            await inner.ExecuteResultAsync(context);
        }
    }
}
