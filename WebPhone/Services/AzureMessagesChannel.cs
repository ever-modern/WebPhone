using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using WebPhone.Contract;
using WebPhone.Registration;

namespace WebPhone.Services;

public sealed class AzureMessagesChannel : IExternalChannel<Message>, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ExchangePath = "/api/exchange";
    private readonly HttpClient client = new();
    private readonly Uri exchangeUri;
    private readonly Channel<Message> incomingChannel = Channel.CreateUnbounded<Message>();
    private readonly Channel<Message> outgoingChannel = Channel.CreateUnbounded<Message>();
    private readonly TimeSpan idleSendInterval;
    private readonly CancellationTokenSource cts = new();
    private readonly Task sendLoopTask;
    private DateTimeOffset lastReadTimestamp = DateTimeOffset.UtcNow.AddSeconds(-5);
    private DateTimeOffset lastSentTimestamp = DateTimeOffset.UtcNow;
    private string clientId = string.Empty;

    public AzureMessagesChannel(string baseUrl, int pollIntervalMs = 1000)
    {
        var baseUri = EnsureTrailingSlash(baseUrl);
        exchangeUri = new Uri(baseUri, ExchangePath.TrimStart('/'));
        idleSendInterval = TimeSpan.FromMilliseconds(Math.Max(pollIntervalMs, 250));
        sendLoopTask = RunSendLoopAsync(cts.Token);
    }

    private static Uri EnsureTrailingSlash(string baseUrl)
    {
        var normalized = baseUrl.EndsWith("/", StringComparison.Ordinal)
            ? baseUrl
            : $"{baseUrl}/";
        return new Uri(normalized, UriKind.Absolute);
    }

    public ChannelWriter<Message> Writer => outgoingChannel.Writer;

    public ChannelReader<Message> Reader => incomingChannel.Reader;

    private async Task RunSendLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var waitForMessageTask = outgoingChannel.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var idleDelayTask = Task.Delay(GetIdleDelay(), cancellationToken);
            var completedTask = await Task.WhenAny(waitForMessageTask, idleDelayTask);

            if (completedTask == waitForMessageTask)
            {
                if (!await waitForMessageTask)
                {
                    break;
                }

                await SendExchangeAsync(DrainOutgoingMessages(), cancellationToken);
                continue;
            }

            // No longer send an empty message; presence messages are now sent by the phone service.
            // Avoid tight loop: if GetIdleDelay is already zero (we would have sent an empty heartbeat),
            // pause for the configured idle interval to prevent busy-spinning.
            if (GetIdleDelay() <= TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(idleSendInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // cancellation requested - break the loop on next iteration
                }
            }
        }
    }

    private TimeSpan GetIdleDelay()
    {
        var elapsedSinceLastSend = DateTimeOffset.UtcNow - lastSentTimestamp;
        if (elapsedSinceLastSend >= idleSendInterval)
        {
            return TimeSpan.Zero;
        }

        return idleSendInterval - elapsedSinceLastSend;
    }

    private async Task SendExchangeAsync(MessageRequest[] outgoingMessages, CancellationToken cancellationToken)
    {
        foreach (var outgoingMessage in outgoingMessages)
        {
            UpdateClientId(outgoingMessage);
        }

        var requestStartTimestamp = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            clientId = Guid.NewGuid().ToString("N");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, exchangeUri)
        {
            Content = JsonContent.Create(new ExchangeRequest(clientId, lastReadTimestamp, outgoingMessages), options: JsonOptions)
        };
        request.Headers.Add("X-Client-Id", clientId);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var exchangeResponse = await response.Content.ReadFromJsonAsync<ExchangeResponse>(JsonOptions, cancellationToken);
        var messages = exchangeResponse?.RelevantMessages;
        if (messages is null)
        {
            lastReadTimestamp = requestStartTimestamp;
            lastSentTimestamp = requestStartTimestamp;
            return;
        }

        foreach (var message in messages)
        {
            await incomingChannel.Writer.WriteAsync(new Message(MessageTypeJsonConverter.FromWireValue(message.Type), message.Payload), cancellationToken);
        }

        lastReadTimestamp = requestStartTimestamp;
        lastSentTimestamp = requestStartTimestamp;
    }

    private MessageRequest[] DrainOutgoingMessages()
    {
        var messages = new List<MessageRequest>();
        while (outgoingChannel.Reader.TryRead(out var message))
        {
            var targetClientId = TryGetTargetClientId(message);
            messages.Add(new MessageRequest(MessageTypeJsonConverter.ToWireValue(message.Type), message.Payload, targetClientId));
        }

        return [.. messages];
    }

    private void UpdateClientId(MessageRequest message)
    {
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        if (TryGetClientId(message.Payload) is { } payloadClientId)
        {
            clientId = payloadClientId;
        }
    }

    private static string? TryGetTargetClientId(Message message)
    {
        if ((message.Type != MessageType.Signal && message.Type != MessageType.ClientSignal) || message.Payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!message.Payload.TryGetProperty("payload", out var innerPayload) || innerPayload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryGetPropertyValue(innerPayload, "toUserId");
    }

    private static string? TryGetClientId(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetPropertyValue(payload, "userId") is { } userId)
        {
            return userId;
        }

        if (payload.TryGetProperty("payload", out var innerPayload) && innerPayload.ValueKind == JsonValueKind.Object)
        {
            return TryGetPropertyValue(innerPayload, "fromUserId") ?? TryGetPropertyValue(innerPayload, "userId");
        }

        return null;
    }

    private static string? TryGetPropertyValue(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        outgoingChannel.Writer.TryComplete();
        incomingChannel.Writer.TryComplete();
        client.Dispose();

        await sendLoopTask.ContinueWith(_ => Task.CompletedTask);

        cts.Dispose();
    }
}
