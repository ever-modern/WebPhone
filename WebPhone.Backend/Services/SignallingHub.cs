using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using WebPhone.Domain;
using WebPhone.Domain.Communication;

namespace WebPhone.Backend.Services;

public class SignallingHub(
    ILogger<SignallingHub> logger,
    ConnectedUsersStorage users
) : Hub
{
    public async Task NotifyClientAsync(
        string peerId,
        ExchangeResponse exchangeResponse
    )
    {
        var client = Clients.User(peerId);
        await client.SendAsync(MessageSpecifications.Push.Key, exchangeResponse);
    }

    [HubMethodName(nameof(MessageSpecifications.Send))]
    public async Task NotifyServerAsync(
        MessageRequest message
    )
    {
        var now = DateTime.UtcNow;

        MessageRequest[] messages = [message];

        var peerId = Context.UserIdentifier;

        var transmittedMessages = messages.GroupBy(m => m.TargetClientId)
            .Select(group => (group.Key, new ExchangeResponse([..group.Select(i => ToTransmittedMessage(peerId, i, now))])))
            .ToArray();

        foreach (var (receiverId, exchangeResponse) in transmittedMessages)
        {
            if (receiverId is null)
            {
                var concernedUsers = users.Users.Where(u => u != peerId).ToArray();
                await Clients.Users(concernedUsers).SendAsync(MessageSpecifications.Push.Key, exchangeResponse);
                continue;
            }

            var receiver = Clients.User(receiverId);
            await receiver.SendAsync(
                MessageSpecifications.Push.Key,
                exchangeResponse
            );
        }
    }

    public override Task OnConnectedAsync()
    {
        if (Context.UserIdentifier is not null)
            users.Connected(Context.UserIdentifier, Context.ConnectionId);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.UserIdentifier is not null)
            users.Disconnected(Context.UserIdentifier, Context.ConnectionId);
        return Task.CompletedTask;
    }

    static MessageResponse ToTransmittedMessage(string peerId, MessageRequest incoming, DateTime time) => new MessageResponse(
        Id: -1,
        PublisherClientId: peerId,
        Type: incoming.Type,
        DateTime: time,
        Payload: incoming.Payload
    );
}
