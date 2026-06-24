using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using EverModern.Threading.Channels;

namespace WebPhone.Services.Channels;

public enum RtcMessageType
{
    Ping,
    Disconnect,
    User,
    WantCall,
    ContinueCall,
    StopCall,
    RejectCall,
    WantVideoCall,
}

public record struct RtcMessage(
    RtcMessageType Type,
    string? Payload = null
)
{
    public static RtcMessage Create(RtcMessageType type, object payload) => new RtcMessage(type, JsonSerializer.Serialize(payload));
}

public class RtcConnectionMessageChannel
    : IBroadcastChannel<RtcMessage, RtcMessage>,
        IAsyncDisposable,
        IDisposable
{
    readonly IRtcConnection _rtcConnection;
    readonly List<Channel<RtcMessage>> _incoming = [];
    readonly Channel<RtcMessage> _outgoing = Channel.CreateUnbounded<RtcMessage>();
    readonly CancellationTokenSource _cts = new();
    readonly Task _initializeTask;
    readonly Task _sendLoopTask;
    Subscription? _bytesSubscription;
    bool _isDisposed;

    public RtcConnectionMessageChannel(IRtcConnection rtcConnection)
    {
        _rtcConnection = rtcConnection;
        _initializeTask = InitializeAsync(_cts.Token);
        _sendLoopTask = RunSendLoopAsync(_cts.Token);
    }

    public ChannelWriter<RtcMessage> Writer => _outgoing.Writer;

    public IChannelSubscription<RtcMessage> Subscribe() => Subscribe(_ => true);

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
        catch {}

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
        catch {}

        _cts.Dispose();
    }

    Task InitializeAsync(CancellationToken ct)
    {
        _bytesSubscription = _rtcConnection.BytesReceived.Subscribe(OnBytesReceived);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    void OnBytesReceived(byte[] bytes)
    {
        if (!TryParseWireMessage(bytes, out var message))
            return;

        BroadcastIncoming(message);
    }

    async Task RunSendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var msg in _outgoing.Reader.ReadAllAsync(cancellationToken))
            {
                var written = await _rtcConnection.WriteBytesAsync(ToWireMessage(msg));
                if (written is false)
                    return;
            }
        }
        catch (OperationCanceledException) {}
    }

    void BroadcastIncoming(RtcMessage message)
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

    static byte[] ToWireMessage(RtcMessage message) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

    static bool TryParseWireMessage(byte[] rawMessage, out RtcMessage parsed)
    {
        try
        {
            var text = Encoding.UTF8.GetString(rawMessage);
            var result = JsonSerializer.Deserialize<RtcMessage>(text);
            parsed = result;
            return true;
        }
        catch
        {
            parsed = default!;
            return false;
        }
    }

    public void Dispose() => _ = DisposeAsync();
}
