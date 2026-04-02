using EverModern.Blazor.DirectCommunication;
using System.Text;
using System.Threading.Channels;
using WebPhone.Registration;

namespace WebPhone.Services;

public class RtcMessageChannel : IBroadcastChannel<RtcTextMessage, RtcTextMessage>, IAsyncDisposable
{
    private readonly IRtcConnection connection;
    private readonly WebRtcInterop webRtc;
    private readonly List<Channel<RtcTextMessage>> _incoming = [];
    private readonly Channel<RtcTextMessage> _outgoing = Channel.CreateUnbounded<RtcTextMessage>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _sendLoopTask;
    private bool _isDisposed;

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
                await webRtc.SendMessageBytesAsync(connection.Id, ToWireMessage(msg));
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
     