namespace WebPhone.Contract;

/// <summary>A single chat message returned by the backend.</summary>
public record ChatMessageDto(
    long Id,
    string SenderId,
    string RecipientId,
    string Text,
    DateTime SentAt);
