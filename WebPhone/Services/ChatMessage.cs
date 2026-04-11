namespace WebPhone.Services;

public sealed record ChatMessage(string Sender, string Text, bool IsOwn);
