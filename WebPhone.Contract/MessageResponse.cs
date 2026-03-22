using System.Text.Json;

namespace WebPhone.Contract;

public record MessageResponse(string PublisherClientId, string Type, DateTimeOffset DateTime, JsonElement Payload);