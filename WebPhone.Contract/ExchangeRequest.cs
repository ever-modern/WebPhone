namespace WebPhone.Domain;

public record ExchangeRequest(string ClientId, long MessagesSinceId, MessageRequest[] Messages);
