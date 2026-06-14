using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using WebPhone.Domain;
using WebPhone.Domain.Communication;

namespace WebPhone.Backend.Services;

public class SignallingHub(
    ILogger<SignallingHub> logger
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
        var time = DateTime.UtcNow;

        MessageRequest[] messages = [message];

        var peerId = Context.UserIdentifier;

        logger.LogInformation($"PeerId: {peerId} sending  {messages.Length} messages");

        var everyoneRelatedMessages = messages.Where(m => m.TargetClientId is null).ToArray();

        var messagesByReceiver = messages.Where(m => m.TargetClientId is not null)
            .GroupBy(m => m.TargetClientId)
            .ToDictionary(g => g.Key!, g => g.ToArray());

        foreach (var (receiverId, messagesForReceiver) in messagesByReceiver)
        {
            var receiver = Clients.User(receiverId);
            var messagesToSend = messagesForReceiver.Concat(everyoneRelatedMessages)
                .Select(m => new MessageResponse(
                        Id: -1,
                        PublisherClientId: peerId,
                        Type: m.Type,
                        DateTime: time,
                        Payload: m.Payload
                    )
                )
                .ToArray();

            await receiver.SendAsync(
                MessageSpecifications.Push.Key,
                new ExchangeResponse(messagesToSend)
            );
        }
    }
}
