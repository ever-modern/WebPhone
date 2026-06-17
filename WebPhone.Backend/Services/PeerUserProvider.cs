using Microsoft.AspNetCore.SignalR;

namespace WebPhone.Backend.Services;

public class PeerUserProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        var userId = connection.GetHttpContext()
            ?
            .Request
            .Query["clientId"]
            .FirstOrDefault();

        return userId;
    }
}
