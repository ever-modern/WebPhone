using System.Text.Json;
using WebPhone.Backend.Services;
using WebPhone.Domain;

namespace WebPhone.Backend.Actions;

public sealed record NotifyActionInput(string SenderClientId, NotifyRequest? Request);

public sealed record NotifyActionOutput(bool Success, string TargetClientId);

public sealed class NotifyApiAction(PushNotifier pushNotificationService)
    : ApiActionConcrete<NotifyActionInput, NotifyActionOutput>
{
    public override string Route => "/notify";

    public override async Task<NotifyActionOutput> ExecuteAsync(
        NotifyActionInput input,
        CancellationToken cancellationToken = default)
    {
        var targetClientId = string.IsNullOrWhiteSpace(input.Request?.TargetClientId)
            ? input.SenderClientId
            : input.Request.TargetClientId;

        var message = string.IsNullOrWhiteSpace(input.Request?.Message)
            ? $"Notification from {input.SenderClientId}."
            : input.Request.Message;

        var sent = await pushNotificationService.PushToClientAsync(targetClientId!, message, cancellationToken);
        return new NotifyActionOutput(sent, targetClientId!);
    }
}
