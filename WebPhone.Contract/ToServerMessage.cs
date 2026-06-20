using System.Text.Json;

namespace WebPhone.Domain;

public record MessageContent(
    MessageType Type,
    JsonElement Payload
);