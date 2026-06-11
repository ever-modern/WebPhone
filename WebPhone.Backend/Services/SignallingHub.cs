using Microsoft.AspNetCore.SignalR;
using WebPhone.Domain;
using WebPhone.Domain.Communication;

namespace WebPhone.Backend.Services;

public class SignallingHub : Hub
{
    public async Task NotifyClientAsync(
        string peerId,
        ExchangeResponse exchangeResponse,
        CancellationToken cancellationToken
    )
    {
        var client = Clients.User(peerId);
        await client.SendAsync(MessageSpecifications.Push.Key, exchangeResponse, cancellationToken);
    }

    [HubMethodName(nameof(MessageSpecifications.Send))]
    public async Task NotifyServerAsync(
        string peerId,
        ExchangeRequest exchangeRequest,
        CancellationToken cancellationToken
    )
    {
        var time = DateTime.UtcNow;

        var messagesByReceiver = exchangeRequest
            .Messages.Where(m => m.TargetClientId is not null)
            .GroupBy(m => m.TargetClientId)
            .ToDictionary(g => g.Key!, g => g.ToArray());

        foreach (var (receiverId, messages) in messagesByReceiver)
        {
            var receiver = Clients.User(receiverId);
            var messagesToSend = messages
                .Select(m => new MessageResponse(-1, peerId, m.Type, time, m.Payload))
                .ToArray();

            await receiver.SendAsync(
                MessageSpecifications.Push.Key,
                new ExchangeResponse(messagesToSend),
                cancellationToken
            );
        }
    }
}
