using System.Text.Json;
using System.Threading.Channels;
using WebPhone.Registration;

namespace WebPhone.Services;

public sealed class MockNetlifyMessagesChannel : IExternalChannel<Message>, IAsyncDisposable
{
    private readonly Channel<Message> incomingChannel = Channel.CreateUnbounded<Message>();
    private readonly Channel<Message> outgoingChannel = Channel.CreateUnbounded<Message>();
    private readonly CancellationTokenSource cts = new();
    private readonly PeriodicTimer presenceTimer = new(TimeSpan.FromSeconds(10));
    private readonly Task presenceLoopTask;
    private readonly Task outgoingLoopTask;

    public MockNetlifyMessagesChannel()
    {
        presenceLoopTask = RunPresenceLoopAsync(cts.Token);
        outgoingLoopTask = RunOutgoingLoopAsync(cts.Token);
    }

    public ChannelWriter<Message> Writer => outgoingChannel.Writer;

    public ChannelReader<Message> Reader => incomingChannel.Reader;

    private async Task RunOutgoingLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in outgoingChannel.Reader.ReadAllAsync(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunPresenceLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

            var users = new[]
            {
                new MockUser("debug-ava", "Ava"),
                new MockUser("debug-oliver", "Oliver"),
                new MockUser("debug-zoe", "Zoe")
            };

            while (!cancellationToken.IsCancellationRequested)
            {
                var timestamp = DateTimeOffset.UtcNow;
                foreach (var user in users)
                {
                    await PublishPresenceAsync(user, timestamp, cancellationToken);
                }

                await presenceTimer.WaitForNextTickAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PublishPresenceAsync(MockUser user, DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        var presencePayload = new PresencePayload(user.UserId, user.Name, timestamp);
        var signalPayload = new Message(MessageType.Presence, JsonSerializer.SerializeToElement(presencePayload));
        var message = new Message(MessageType.Signal, JsonSerializer.SerializeToElement(signalPayload));
        await incomingChannel.Writer.WriteAsync(message, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        presenceTimer.Dispose();
        outgoingChannel.Writer.TryComplete();
        incomingChannel.Writer.TryComplete();

        await Task.WhenAll(
            presenceLoopTask.ContinueWith(_ => Task.CompletedTask),
            outgoingLoopTask.ContinueWith(_ => Task.CompletedTask));

        cts.Dispose();
    }

    private sealed record MockUser(string UserId, string Name);

    private sealed record PresencePayload(string UserId, string Name, DateTimeOffset Timestamp);
}
