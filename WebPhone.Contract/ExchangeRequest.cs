namespace WebPhone.Contract;

public record ExchangeRequest(string ClientId, long MessagesSinceId, MessageRequest[] Messages);
