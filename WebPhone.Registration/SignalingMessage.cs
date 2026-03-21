using System.Text.Json;

namespace WebPhone.Registration;

public sealed record SignalingMessage<T>(MessageType Type, T Payload);

public sealed record SignalingMessage(MessageType Type, JsonElement Payload);
