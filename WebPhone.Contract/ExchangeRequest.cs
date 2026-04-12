namespace WebPhone.Contract;

public record ExchangeRequest(string ClientId, DateTime MessagesActualityCutoffDate, MessageRequest[] Messages);
