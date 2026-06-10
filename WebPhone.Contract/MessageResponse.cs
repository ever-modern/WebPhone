using System.Text.Json;

namespace WebPhone.Domain;

public record MessageResponse(long Id, string PublisherClientId, string Type, DateTime DateTime, JsonElement Payload);