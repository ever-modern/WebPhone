using System.Text.Json;

namespace WebPhone.Domain;


public record MessageRequest(string Type, JsonElement Payload, string? TargetClientId = null);
