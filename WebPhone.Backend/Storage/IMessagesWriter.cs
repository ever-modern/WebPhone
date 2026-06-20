using Microsoft.AspNetCore.SignalR;
using WebPhone.Backend.Services;
using WebPhone.Domain;
using WebPhone.Domain.Communication;

namespace WebPhone.Backend.Storage;

public interface IMessagesWriter
{
    Task WriteAsync(string targetId, string senderId, MessageContent messageContent, CancellationToken cancellationToken);
}

public class HubMessagesChannel(
    IHubContext<SignallingHub> hub
) : IMessagesWriter
{
    public async Task WriteAsync(string targetId, string senderId, MessageContent messageContent, CancellationToken cancellationToken)
    {
        ReceivedMessage message = new(
            senderId,
            messageContent.Type,
            messageContent.Payload
        );

        await hub.Clients.User(targetId)
            .SendAsync(
                MessageSpecifications.Push.Key,
                message,
                cancellationToken
            );
    }
}
