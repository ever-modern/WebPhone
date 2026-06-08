using EverModern.Blazor.DirectCommunication;
using System.Text.Json;

namespace WebPhone.Messages;

public record RtcTextMessage(string Text, bool IsSystem);

public record FavoriteContact(string Id, string Name);

public sealed record UserPresence(string UserId, string Name, DateTimeOffset LastSeen);

public sealed record PresencePayload(string Name);

public sealed record HungupPayload(string CallId);

public sealed record ConnectionRequestPayload(string RequestId, WebRtcOffer Offer);

public sealed record AnswerPayload(string RequestId, WebRtcAnswer Answer);

public sealed record InitiateCallPayload(string ConnectionId);

public sealed record CallResponsePayload(string ConnectionId, bool Accepted);

public sealed record ChatMessage(string Sender, string Text, bool IsOwn);

public sealed record ConnectionRejectedPayload(string RequestId);

public record OutgoingMessage<T>(WebPhone.Contract.MessageType Type, T Payload, string? TargetClientId)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static implicit operator OutgoingMessage(OutgoingMessage<T> self)
        => new(self.Type, JsonSerializer.SerializeToElement(self.Payload, SerializerOptions), self.TargetClientId);
}

public record OutgoingMessage(WebPhone.Contract.MessageType Type, JsonElement Payload, string? TargetClientId)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public OutgoingMessage<T>? SpecifyPayload<T>()
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<T>(Payload, SerializerOptions);
            return parsed is null ? default : new(Type, parsed, TargetClientId);
        }
        catch { return default; }
    }
}

public record IncomingMessage<T>(long Id, WebPhone.Contract.MessageType Type, T Payload, string SenderClientId, DateTime DateTime);

public record IncomingMessage(long Id, WebPhone.Contract.MessageType Type, JsonElement Payload, string SenderClientId, DateTime DateTime)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public IncomingMessage<T>? SpecifyPayload<T>()
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<T>(Payload, SerializerOptions);
            return parsed is null ? default : new(Id, Type, parsed, SenderClientId, DateTime);
        }
        catch { return default; }
    }
}