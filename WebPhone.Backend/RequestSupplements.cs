namespace WebPhone.Backend;

public record RequestSupplements(string? ClientId)
{
    public string RequireClientId() => ClientId ?? throw new UserFaultException("No client id provided.");
}
