using System.Text.Json;
using WebPhone.Messages;
using WebPhone.Services.Channels;
using WebPhone.Services.Data;

namespace WebPhone.Services.Background;

public sealed class PresenceAnnouncer(
    IMessagesChannel messagesChannel,
    IProfile profile,
    PhoneOptions options
) : IAsyncDisposable
{
    private readonly int _pollIntervalMs = Math.Max(options.PollIntervalMs, 250);
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public async Task StartAsync()
    {
        await SendPresenceAsync();

        _cts = new CancellationTokenSource();
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_pollIntervalMs));
        _loopTask = Task.Run(async () =>
        {
            while (await timer!.WaitForNextTickAsync(_cts.Token))
            {
                await SendPresenceAsync();
            }
        });
    }

    private async Task SendPresenceAsync()
    {
        if (string.IsNullOrWhiteSpace(profile.User.Name))
        {
            return;
        }

        var payload = JsonSerializer.SerializeToElement(new PresencePayload(profile.User.Name));
        await messagesChannel.Writer.WriteAsync(
            new OutgoingMessage(MessageType.Presence, payload, null)
        );
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loopTask is not null)
            await _loopTask;
        _cts?.Dispose();
    }
}
