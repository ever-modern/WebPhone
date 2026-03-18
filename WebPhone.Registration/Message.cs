using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WebPhone.Registration;

public record Message<T>(string Type, T Payload);

public record Message(string Type, JsonElement Payload);
