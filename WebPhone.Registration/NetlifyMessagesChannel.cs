using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;

namespace WebPhone.Registration;

public sealed class NetlifyMessagesChannel: IExternalChannel<Message>, IAsyncDisposable
{
    private readonly HttpClient client;
    private readonly Channel<Message> incomingChannel = Channel.CreateUnbounded<Message>();
    private readonly Channel<Message> outgoingChannel = Channel.CreateUnbounded<Message>();
    private readonly Uri pollUri;
    private readonly PeriodicTimer pollTimer;
    private readonly CancellationTokenSource cts = new();
    private readonly Task sendLoopTask;
    private readonly Task pollLoopTask;

    public NetlifyMessagesChannel(string baseUrl, string pollPath = "poll", int pollIntervalMs = 1000)
    {
        client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl)
        };

        pollUri = new Uri(client.BaseAddress!, pollPath);
        pollTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(pollIntervalMs, 250)));
        sendLoopTask = RunSendLoopAsync(cts.Token);
        pollLoopTask = RunPollLoopAsync(cts.Token);
    }

    public ChannelWriter<Message> Writer => outgoingChannel.Writer;

    public ChannelReader<Message> Reader => incomingChannel.Reader;

    private async Task RunSendLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var message in outgoingChannel.Reader.ReadAllAsync(cancellationToken))
        {
            var response = await client.PostAsJsonAsync(string.Empty, message, cancellationToken);
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
        using var response = await client.GetAsync(pollUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var messages = await JsonSerializer.DeserializeAsync<Message[]>(stream, cancellationToken: cancellationToken);
        if (messages is null)
        {
            return;
        }

        foreach (var message in messages)
        {
            await incomingChannel.Writer.WriteAsync(message, cancellationToken);
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
}
