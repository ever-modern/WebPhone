namespace WebPhone.Contract;

public record ExchangeResponse(MessageResponse[] RelevantMessages, DateTime WrittenAt);
