using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using WebPhone.Registration;

namespace WebPhone.Registration;

public sealed class AzureMessagesChannel : IExternalChannel<Message>, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string PublishPath = "/api/publish-message";
    private const string ReadPath = "/api/read-messages";
    private readonly HttpClient client = new();
    private readonly Uri publishUri;
    private readonly Uri readUri;
    private readonly Channel<Message> incomingChannel = Channel.CreateUnbounded<Message>();
    private readonly Channel<Message> outgoingChannel = Channel.CreateUnbounded<Message>();
    private readonly PeriodicTimer pollTimer;
    private readonly CancellationTokenSource cts = new();
    private readonly Task sendLoopTask;
    private readonly Task pollLoopTask;
    private DateTimeOffset lastReadTimestamp = DateTimeOffset.UtcNow.AddSeconds(-5);

    public AzureMessagesChannel(string baseUrl, int pollIntervalMs = 1000)
    {
        var baseUri = EnsureTrailingSlash(baseUrl);
        publishUri = new Uri(baseUri, PublishPath.TrimStart('/'));
        readUri = new Uri(baseUri, ReadPath.TrimStart('/'));
        pollTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(pollIntervalMs, 250)));
        sendLoopTask = RunSendLoopAsync(cts.Token);
        pollLoopTask = RunPollLoopAsync(cts.Token);
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
        await foreach (var message in outgoingChannel.Reader.ReadAllAsync(cancellationToken))
        {
            using var response = await client.PostAsJsonAsync(publishUri, message, JsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task RunPollLoopAsync(CancellationToken cancellationToken)
    {
        while (await pollTimer.WaitForNextTickAsync(cancellationToken))
        {
            await PollAsync(cancellationToken);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        var uri = new UriBuilder(readUri)
        {
            Query = $"since={Uri.EscapeDataString(lastReadTimestamp.ToString("O"))}"
        };

        var messages = await client.GetFromJsonAsync<MessageEnvelope[]>(uri.Uri, JsonOptions, cancellationToken);
        if (messages is null || messages.Length == 0)
        {
            return;
        }

        foreach (var message in messages)
        {
            lastReadTimestamp = message.DateTime > lastReadTimestamp ? message.DateTime : lastReadTimestamp;
            await incomingChannel.Writer.WriteAsync(new Message(message.Type, message.Payload), cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        pollTimer.Dispose();
        outgoingChannel.Writer.TryComplete();
        incomingChannel.Writer.TryComplete();
        client.Dispose();

        await Task.WhenAll(
            sendLoopTask.ContinueWith(_ => Task.CompletedTask),
            pollLoopTask.ContinueWith(_ => Task.CompletedTask));

        cts.Dispose();
    }

    private sealed record MessageEnvelope(DateTimeOffset DateTime, MessageType Type, JsonElement Payload);
}
