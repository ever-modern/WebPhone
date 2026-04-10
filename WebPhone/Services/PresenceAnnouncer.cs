using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WebPhone.Services;

public sealed class PresenceAnnouncer(
    IMessagesChannel messagesChannel,
    IProfile profile,
    PhoneOptions options,
    ILogger<PresenceAnnouncer> logger) : IAsyncDisposable
{
    private readonly int _pollIntervalMs = Math.Max(options.PollIntervalMs, 250);
    private DateTimeOffset _lastSentAt = DateTimeOffset.UtcNow;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public async Task StartAsync()
    {
        if (!string.IsNullOrWhiteSpace(profile.User.Name))
            await SendPresenceAsync();

        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        _loopTask = RunLoopAsync(_cts.Token);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                if (!await _timer!.WaitForNextTickAsync(ct))
                    break;

                if (DateTimeOffset.UtcNow - _lastSentAt >= TimeSpan.FromMilliseconds(_pollIntervalMs))
                    await SendPresenceAsync();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[PresenceAnnouncer] Failed to send presence — retrying on next tick");
            }
        }
    }

    private async Task SendPresenceAsync()
    {
        var payload = JsonSerializer.SerializeToElement(new PresencePayload(profile.User.Name));
        await messagesChannel.Writer.WriteAsync(new OutgoingMessage(MessageType.Presence, payload, null));
        _lastSentAt = DateTimeOffset.UtcNow;
        logger.LogDebug("[PresenceAnnouncer] Presence sent for {Name}", profile.User.Name);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        if (_loopTask is not null)
            await _loopTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _cts?.Dispose();
    }
}
