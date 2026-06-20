using System.Text.Json;

namespace WebPhone.Domain;

public record SentMessage(
    string? Receiver,
    MessageType Type,
    JsonElement Payload
) : MessageContent(Type, Payload);

public record TransmittedMessage(
    string Receiver,
    string Sender,
    MessageType Type,
    JsonElement Payload
) : MessageContent(Type, Payload);
