using Microsoft.AspNetCore.SignalR;
using WebPhone.Domain;
using WebPhone.Domain.Communication;

namespace WebPhone.Backend.Services;

public class SignallingHub(
    ConnectedUsersStorage users
) : Hub
{
    [HubMethodName(nameof(MessageSpecifications.Send))]
    public async Task NotifyOthersAsync(
        SentMessage message
    )
    {
        var sender = Context.UserIdentifier!;
        var receiver = message.Receiver;

        var messageForClient = new ReceivedMessage(sender, message.Type, message.Payload);

        if (receiver is null)
        {
            var concernedUsers = users.Users.Where(u => u != sender).ToArray();
            await Clients.Users(concernedUsers).SendAsync(nameof(MessageSpecifications.Push), messageForClient);
            return;
        }

        await Clients.User(receiver)
            .SendAsync(
                MessageSpecifications.Push.Key,
                messageForClient
            );
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
}
