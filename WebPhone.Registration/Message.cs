using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebPhone.Registration;

public record Message<T>(MessageType Type, T Payload);

public record Message(MessageType Type, JsonElement Payload);

[JsonConverter(typeof(MessageTypeJsonConverter))]
public enum MessageType
{
    Unknown,
    Pusher,
    ClientSignal,
    Signal,
    Presence,
    Hangup,
    Call,
    Accept,
    Offer,
    Answer
}

public sealed class MessageTypeJsonConverter : JsonConverter<MessageType>
{
    public static string ToWireValue(MessageType value)
        => value switch
        {
            MessageType.Pusher => "pusher",
            MessageType.ClientSignal => "client-signal",
            MessageType.Signal => "signal",
            MessageType.Presence => "presence",
            MessageType.Hangup => "hangup",
            MessageType.Call => "call",
            MessageType.Accept => "accept",
            MessageType.Offer => "offer",
            MessageType.Answer => "answer",
            _ => "unknown"
        };

    public static MessageType FromWireValue(string? value)
        => value?.ToLowerInvariant() switch
        {
            "pusher" => MessageType.Pusher,
            "client-signal" => MessageType.ClientSignal,
            "signal" => MessageType.Signal,
            "presence" => MessageType.Presence,
            "hangup" => MessageType.Hangup,
            "call" => MessageType.Call,
            "accept" => MessageType.Accept,
            "offer" => MessageType.Offer,
            "answer" => MessageType.Answer,
            _ => MessageType.Unknown
        };

    public override MessageType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            return MessageType.Unknown;
        }

        return FromWireValue(reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, MessageType value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToWireValue(value));
}
