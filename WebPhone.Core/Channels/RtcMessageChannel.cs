using System.Text;
using System.Text.Json;
using EverModern.Blazor.DirectCommunication;
using EverModern.Events;

namespace WebPhone.Channels;

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

public record struct RtcMessage(RtcMessageType Type, string? Payload = null)
{
    public static RtcMessage Create(RtcMessageType type, object payload) =>
        new RtcMessage(type, JsonSerializer.Serialize(payload));
}

public class RtcConnectionMessageChannel(BytesChannel bytesChannel) : IDisposable
{
    readonly EventSource<RtcMessage> _received = CreateTransformer(bytesChannel.Received);

    public INotifier<RtcMessage> Received => _received;

    public ValueTask<bool> WriteAsync(RtcMessage message)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        return bytesChannel.WriteAsync(bytes);
    }

    bool _isDisposed;

    static EventSource<RtcMessage> CreateTransformer(INotifier<byte[]> incoming)
    {
        EventSource<RtcMessage> result = new();
        incoming.Subscribe(bytes =>
        {
            if (TryParseWireMessage(bytes, out RtcMessage parsed))
            {
                result.Invoke(parsed);
            }
        });
        return result;
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

    public void Dispose() => _received.Dispose();
}
