using WebPhone.Backend.Storage;

namespace WebPhone.Backend.Actions;

public sealed record SubscriptionActionInput(string ClientId, PushSubscriptionDto Subscription);

public sealed record SubscriptionActionOutput(bool Success);

public sealed class SubscriptionApiAction(PushSubscriptionsRepository subscriptions)
    : ApiActionConcrete<SubscriptionActionInput, SubscriptionActionOutput>
{
    public override string Route => "/subscribe-for-push";

    public override async Task<SubscriptionActionOutput> ExecuteAsync(
        SubscriptionActionInput input,
        CancellationToken cancellationToken = default)
    {
        await subscriptions.InsertOrUpdateAsync(input.ClientId, input.Subscription, cancellationToken);
        return new SubscriptionActionOutput(true);
    }
}
