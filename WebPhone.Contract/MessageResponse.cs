using System.Text.Json;

namespace WebPhone.Contract;

public record MessageResponse(long Id, string PublisherClientId, string Type, DateTime DateTime, JsonElement Payload);