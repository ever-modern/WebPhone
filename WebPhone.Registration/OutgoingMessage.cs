using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebPhone.Registration;

public record OutgoingMessage<T>(MessageType Type, T Payload, string? TargetClientId)
{
    public static implicit operator OutgoingMessage(OutgoingMessage<T> self) 
        => new(self.Type, JsonSerializer.SerializeToElement(self.Payload), self.TargetClientId);
}

public record OutgoingMessage(MessageType Type, JsonElement Payload, string? TargetClientId)
{
    public OutgoingMessage<T>? SpecifyPayload<T>()
    {
        try { return new(Type, JsonSerializer.Deserialize<T>(Payload), TargetClientId); }
        catch { return default; }
    }
}

public record IncomingMessage<T>(MessageType Type, T Payload, string SenderClientId, DateTimeOffset DateTime);

public record IncomingMessage(MessageType Type, JsonElement Payload, string SenderClientId, DateTimeOffset DateTime)
{
    public IncomingMessage<T>? SpecifyPayload<T>()
    {
        try { return new(Type, JsonSerializer.Deserialize<T>(Payload), SenderClientId, DateTime); }
        catch { return default; }
    }
}


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
