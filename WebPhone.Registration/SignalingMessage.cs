using System.Text.Json;

namespace WebPhone.Registration;

public sealed record SignalingMessage<T>(string Type, T Payload);

public sealed record SignalingMessage(string Type, JsonElement Payload);
