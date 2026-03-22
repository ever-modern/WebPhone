using System.Text.Json;

namespace WebPhone.Contract;

public record MessageRequest(string Type, JsonElement Payload, string? TargetClientId = null);
