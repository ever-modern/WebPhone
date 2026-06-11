using Microsoft.AspNetCore.SignalR;

namespace WebPhone.Backend.Services;

public class PeerUserProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.GetHttpContext()?
            .Request
            .Headers["X-Client-Id"]
            .FirstOrDefault();
}