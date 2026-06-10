using WebPhone.Backend.Storage;
using WebPhone.Domain;

namespace WebPhone.Backend.Actions;

public sealed record ExchangeActionInput(string ClientId, ExchangeRequest Request);

public sealed class ExchangeApiAction(MessagesRepository repository, RequestSupplements requestSupplements)
    : ApiActionConcrete<ExchangeActionInput, ExchangeResponse>
{
    public override string Route => "/exchange";

    public override async Task<ExchangeResponse> ExecuteAsync(
        ExchangeActionInput input,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var clientId = requestSupplements.RequireClientId();

        await repository.WriteMessagesAsync(
        [
            .. input.Request.Messages?.Select(m => new MessageWriteEntry(
                m.Type,
                m.Payload,
                input.ClientId,
                m.TargetClientId,
                now
            )) ?? [],
        ],
        cancellationToken: cancellationToken);

        var relevantMessages = await repository.ReadMessagesAsync(
            new MessagesFilter(
                ReceiverId: clientId,
                SinceId: input.Request.MessagesSinceId,
                ExcludedIds: [input.ClientId]
            ),
            cancellationToken
        );

        return new ExchangeResponse(
            [
                .. relevantMessages.Select(m => new MessageResponse(
                    m.Id,
                    m.PublisherId,
                    m.Type,
                    m.DateTime,
                    m.Payload
                )),
            ]
        );
    }
}
