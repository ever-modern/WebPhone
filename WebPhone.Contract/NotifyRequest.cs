namespace WebPhone.Domain;

public record NotifyRequest(string? TargetClientId, string? Message);
