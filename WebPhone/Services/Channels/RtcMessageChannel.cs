using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using EverModern.Threading.Channels;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace WebPhone.Services.Channels;

public enum RtcMessageType
{
    User,
    WantCall,
    RejectCall
}

public record struct RtcMessage(RtcMessageType Type, string? Payload = null);

public class RtcConnectionMessageChannel : IBroadcastChannel<RtcMessage, RtcMessage>, IAsyncDisposable, IDisposable
{
    readonly RtcConnection _rtcConnection;
    private readonly List<Channel<RtcMessage>> _incoming = [];
    private readonly Channel<RtcMessage> _outgoing = Channel.CreateUnbounded<RtcMessage>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _initializeTask;
    private readonly Task _sendLoopTask;
    private Subscription? _bytesSubscription;
    private bool _isDisposed;

    public RtcConnectionMessageChannel(RtcConnection rtcConnection)
    {
        _rtcConnection = rtcConnection;
        _initializeTask = InitializeAsync(_cts.Token);
        _sendLoopTask = RunSendLoopAsync(_cts.Token);
    }

    public ChannelWriter<RtcMessage> Writer => _outgoing.Writer;

    public IChannelSubscription<RtcMessage> Subscribe() => Subscribe(null);

    public IChannelSubscription<RtcMessage> Subscribe(Func<RtcMessage, bool> filter)
    {
        var channel = Channel.CreateUnbounded<RtcMessage>();

        lock (_incoming)
        {
            _incoming.Add(channel);
        }

        return new ChannelSubscription<RtcMessage>(
            channel.Reader,
            _ =>
            {
                lock (_incoming)
                {
                    _incoming.Remove(channel);
                }

                channel.Writer.TryComplete();
            },
            filter
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _cts.Cancel();

        try
        {
            await _initializeTask;
        }
        catch
        {
        }

        _bytesSubscription?.Dispose();

        lock (_incoming)
        {
            foreach (var incomingChannel in _incoming)
            {
                incomingChannel.Writer.TryComplete();
            }

            _incoming.Clear();
        }

        _outgoing.Writer.TryComplete();
        try
        {
            await _sendLoopTask;
        }
        catch
        {
        }

        _cts.Dispose();
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        _bytesSubscription = await _rtcConnection.SubscribeBytesAsync(OnBytesReceived);
        ct.ThrowIfCancellationRequested();
    }

    private void OnBytesReceived(byte[] bytes)
    {
        if (!TryParseWireMessage(bytes, out var message))
            return;

        BroadcastIncoming(message);
    }

    private async Task RunSendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var msg in _outgoing.Reader.ReadAllAsync(cancellationToken))
            {
                await _rtcConnection.WriteBytesAsync(ToWireMessage(msg));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void BroadcastIncoming(RtcMessage message)
    {
        Channel<RtcMessage>[] subscriptions;
        lock (_incoming)
        {
            subscriptions = [.. _incoming];
        }

        foreach (var channel in subscriptions)
        {
            channel.Writer.TryWrite(message);
        }
    }

    private static byte[] ToWireMessage(RtcMessage message)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

    private static bool TryParseWireMessage(byte[] rawMessage, out RtcMessage parsed)
    {
        try
        {
            var text = Encoding.UTF8.GetString(rawMessage);
            var result = JsonSerializer.Deserialize<RtcMessage>(text);
            if (result is not null)
            {
                parsed = result;
                return true;
            }

            parsed = new(RtcMessageType.User, text);
            return true;
        }
        catch
        {
            parsed = default!;
            return false;
        }
    }

    public void Dispose()
        => _ = DisposeAsync();
}