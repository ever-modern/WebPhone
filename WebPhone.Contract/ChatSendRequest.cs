namespace WebPhone.Domain;

/// <summary>Body for POST /api/chat/send.</summary>
public record ChatSendRequest(string Text, string RecipientId);
