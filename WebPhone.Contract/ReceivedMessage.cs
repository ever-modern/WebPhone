using System.Text.Json;

namespace WebPhone.Domain;

public record ReceivedMessage(
    string Sender,
    MessageType Type,
    JsonElement Payload
) : MessageContent(Type, Payload);