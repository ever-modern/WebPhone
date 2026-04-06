namespace WebPhone.Contract;

public record NotifyRequest(string? TargetClientId, string? Message);
