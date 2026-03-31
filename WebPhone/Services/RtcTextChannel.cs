using EverModern.Blazor.DirectCommunication;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WebPhone.Registration;

namespace WebPhone.Services;

public class RtcTextChannel(IRtcConnection connection, WebRtcInterop webRtc) : IBroadcastChannel<RtcTextMessage, RtcTextMessage>, IAsyncDisposable
{
    private readonly Channel<RtcTextMessage> _incoming = Channel.CreateUnbounded<RtcTextMessage>();
    private readonly Channel<RtcTextMessage> _outgoing = Channel.CreateUnbounded<RtcTextMessage>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _sendLoopTask;
    private bool _isDisposed;


    public ChannelWriter<RtcTextMessage> Writer => _outgoing.Writer;

    public IChannelSubscription<RtcTextMessage> Subscribe() => new ChannelSubscription(_incoming.Reader);

    public IChannelSubscription<RtcTextMessage> Subscribe(Func<RtcTextMessage, bool> filter) => new ChannelSubscription(_incoming.Reader, filter);

    // Call this when a message is received from WebRTC
    public void OnMessageReceived(RtcTextMessage message)
    {
        _incoming.Writer.TryWrite(message);
    }

    private async Task RunSendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var msg in _outgoing.Reader.ReadAllAsync(cancellationToken))
            {
                await webRtc.SendMessageAsync(connection.Id, msg.Text);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _cts.Cancel();
        _incoming.Writer.TryComplete();
        _outgoing.Writer.TryComplete();
        try { await _sendLoopTask; } catch { }
        _cts.Dispose();
    }

    private class ChannelSubscription : IChannelSubscription<RtcTextMessage>
    {
        private readonly ChannelReader<RtcTextMessage> _reader;
        private readonly Func<RtcTextMessage, bool>? _filter;

        public ChannelSubscription(ChannelReader<RtcTextMessage> reader, Func<RtcTextMessage, bool>? filter = null)
        {
            _reader = reader;
            _filter = filter;
        }

        public async IAsyncEnumerable<RtcTextMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var msg in _reader.ReadAllAsync(cancellationToken))
            {
                if (_filter == null || _filter(msg))
                    yield return msg;
            }
        }

        public void Dispose() { }

        public ValueTask<RtcTextMessage> ReadAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
     