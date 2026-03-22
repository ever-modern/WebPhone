namespace WebPhone.Contract;

public record ExchangeRequest(string ClientId, DateTimeOffset MessagesActualityCutoffDate, MessageRequest[] Messages);
