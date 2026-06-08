using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebPhone.Contract;


[JsonConverter(typeof(MessageTypeJsonConverter))]
public enum MessageType
{
    Unknown,
    Signal,
    Presence,
    Hangup,
    ConnectionAttempt,
    ConnectionAccepted,
    ConnectionRejected,
    ConnectionClosed,
    Call,
    CallResponse
}

public sealed class MessageTypeJsonConverter : JsonConverter<MessageType>
{
    public static string ToWireValue(MessageType value)
        => value switch
        {
            MessageType.Presence => "presence",
            MessageType.Hangup => "hangup",
            MessageType.ConnectionAttempt => "connection-attempt",
            MessageType.ConnectionAccepted => "connection-accepted",
            MessageType.ConnectionRejected => "connection-rejected",
            MessageType.ConnectionClosed => "connection-closed",
            _ => "unknown"
        };

    public static MessageType FromWireValue(string? value)
        => value?.ToLowerInvariant() switch
        {
            "presence" => MessageType.Presence ,
            "hangup" => MessageType.Hangup,
            "connection-attempt" => MessageType.ConnectionAttempt,
            "connection-accepted" => MessageType.ConnectionAccepted,
            "connection-rejected" => MessageType.ConnectionRejected,
            "connection-closed" => MessageType.ConnectionClosed,
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
