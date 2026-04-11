using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using EverModern.Threading.Channels;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace WebPhone.Services;

public enum RtcMessageType
{
    User,
    CallRequest,
    AcceptCall,
    MaintainingCall
}

public record RtcMessage(RtcMessageType Type, string? Payload);

public class RtcConnectionMessageChannel : IBroadcastChannel<RtcMessage, RtcMessage>, IAsyncDisposable, IDisposable
{
    readonly RtcConnectionAgent _rtcConnection;
    private readonly List<Channel<RtcMessage>> _incoming = [];
    private readonly Channel<RtcMessage> _outgoing = Channel.CreateUnbounded<RtcMessage>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _initializeTask;
    private readonly Task _sendLoopTask;
    private Subscription? _bytesSubscription;
    private bool _isDisposed;

    public RtcConnectionMessageChannel(RtcConnectionAgent rtcConnection)
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


[Obsolete("", true)]
public class RtcMessageChannel : IBroadcastChannel<RtcTextMessage, RtcTextMessage>, IAsyncDisposable
{
    private readonly IRtcConnection connection;
    private readonly WebRtcInterop webRtc;
    private readonly List<Channel<RtcTextMessage>> _incoming = [];
    private readonly Channel<RtcTextMessage> _outgoing = Channel.CreateUnbounded<RtcTextMessage>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _sendLoopTask;
    private bool _isDisposed;

    public bool IsDisposed => _isDisposed;

    public RtcMessageChannel(IRtcConnection connection, WebRtcInterop webRtc)
    {
        this.connection = connection;
        this.webRtc = webRtc;
        _sendLoopTask = RunSendLoopAsync(_cts.Token);
    }

    public ChannelWriter<RtcTextMessage> Writer => _outgoing.Writer;

    public string ConnectionId => connection.Id;

    public IChannelSubscription<RtcTextMessage> Subscribe() => Subscribe(null);

    public IChannelSubscription<RtcTextMessage> Subscribe(Func<RtcTextMessage, bool> filter)
    {
        var channel = Channel.CreateUnbounded<RtcTextMessage>();

        lock (_incoming)
        {
            _incoming.Add(channel);
        }

        return new ChannelSubscription<RtcTextMessage>(
            channel.Reader,
            _ =>
            {
                lock (_incoming)
                {
                    _incoming.Remove(channel);
                }

                channel.Writer.TryComplete();
            },
            filter);
    }

    // Call this when a message is received from WebRTC
    public void OnMessageReceived(RtcTextMessage message)
    {
        BroadcastIncoming(message);
    }

    public void OnRawMessageReceived(string rawMessage)
    {
        if (TryParseWireMessage(rawMessage, out var parsed))
        {
            BroadcastIncoming(parsed);
            return;
        }

        BroadcastIncoming(new RtcTextMessage(rawMessage, false));
    }

    public void OnRawMessageReceived(byte[] rawMessage)
    {
        if (TryParseWireMessage(rawMessage, out var parsed))
        {
            BroadcastIncoming(parsed);
        }
    }

    private async Task RunSendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var msg in _outgoing.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await webRtc.SendMessageBytesAsync(connection.Id, ToWireMessage(msg));
                }
                catch (Exception) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void BroadcastIncoming(RtcTextMessage message)
    {
        Channel<RtcTextMessage>[] subscriptions;
        lock (_incoming)
        {
            subscriptions = [.. _incoming];
        }

        foreach (var channel in subscriptions)
        {
            channel.Writer.TryWrite(message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _cts.Cancel();
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

    private static byte[] ToWireMessage(RtcTextMessage message)
    {
        var textBytes = Encoding.UTF8.GetBytes(message.Text);
        var result = new byte[textBytes.Length + 1];
        result[0] = message.IsSystem ? (byte)1 : (byte)0;
        textBytes.CopyTo(result, 1);
        return result;
    }

    private static bool TryParseWireMessage(byte[] rawMessage, out RtcTextMessage parsed)
    {
        if (rawMessage.Length == 0)
        {
            parsed = default;
            return false;
        }

        var isSystem = rawMessage[0] == 1;
        var text = rawMessage.Length > 1 ? Encoding.UTF8.GetString(rawMessage, 1, rawMessage.Length - 1) : string.Empty;
        parsed = new RtcTextMessage(text, isSystem);
        return true;
    }

    private static bool TryParseWireMessage(string rawMessage, out RtcTextMessage parsed)
    {
        try
        {
            var bytes = Convert.FromBase64String(rawMessage);
            return TryParseWireMessage(bytes, out parsed);
        }
        catch (FormatException)
        {
            parsed = default;
            return false;
        }
    }

}
     