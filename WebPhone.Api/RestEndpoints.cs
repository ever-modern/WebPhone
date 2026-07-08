using Microsoft.AspNetCore.Http.HttpResults;
using WebPhone.Backend;
using WebPhone.Backend.Actions;
using WebPhone.Backend.Storage;
using WebPhone.Domain;

namespace WebPhone.Api;

public static class RestEndpoints
{
    static string RequireClientId(HttpRequest req) => req.Headers["X-Client-Id"].FirstOrDefault()
        ?? throw new UserFaultException("No \"X-Client-Id\" present.");


    public static void MapRestEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/health",
            async Task<IResult> (HealthCheckApiAction action, CancellationToken ct) =>
            {
                var result = await action.ExecuteAsync(null, ct);
                return result.Healthy
                    ? TypedResults.Ok(result)
                    : TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        );

        app.MapPost(
            "/notify",
            async Task<IResult> (
                HttpRequest req,
                NotifyRequest request,
                NotifyApiAction action,
                CancellationToken ct
            ) =>
            {
                var senderClientId = RequireClientId(req);
                if (string.IsNullOrWhiteSpace(senderClientId))
                    return TypedResults.BadRequest("Missing X-Client-Id header");

                var result = await action.ExecuteAsync(
                    new NotifyActionInput(senderClientId, request),
                    ct
                );
                return TypedResults.Ok(
                    new { success = result.Success, targetClientId = result.TargetClientId }
                );
            }
        );

        app.MapPost(
            "/subscribe-for-push",
            async Task<IResult> (
                HttpRequest req,
                PushSubscriptionDto subscription,
                SubscriptionApiAction action,
                CancellationToken ct
            ) =>
            {
                var clientId = RequireClientId(req);
                if (string.IsNullOrWhiteSpace(clientId))
                    return TypedResults.BadRequest("Missing X-Client-Id header");

                if (string.IsNullOrWhiteSpace(subscription.Endpoint))
                    return TypedResults.BadRequest("Missing required subscription fields");

                var result = await action.ExecuteAsync(
                    new SubscriptionActionInput(clientId, subscription),
                    ct
                );
                return TypedResults.Ok(new { success = result.Success });
            }
        );

        app.MapGet(
            "/profiles",
            async Task<Results<BadRequest<string>, Ok<UserSettingsDto>>> (
                HttpRequest req,
                GetProfileSettingsApiAction action,
                CancellationToken ct
            ) =>
            {
                var ownerId = RequireClientId(req);
                if (string.IsNullOrWhiteSpace(ownerId))
                    return TypedResults.BadRequest("Missing X-Client-Id header");

                var result = await action.ExecuteAsync(new GetProfileSettingsInput(ownerId), ct);
                return TypedResults.Ok(result);
            }
        );

        app.MapPost(
            "/profiles",
            async Task<IResult> (
                HttpRequest req,
                UserSettingsDto body,
                UpsertProfileSettingsApiAction action,
                CancellationToken ct
            ) =>
            {
                var ownerId = RequireClientId(req);
                if (string.IsNullOrWhiteSpace(ownerId))
                    return TypedResults.BadRequest("Missing X-Client-Id header");

                await action.ExecuteAsync(new UpsertProfileSettingsInput(ownerId, body), ct);
                return TypedResults.Ok(new { success = true });
            }
        );

        app.MapGet(
            "/contacts",
            async Task<IResult> (
                HttpRequest req,
                GetContactSettingsApiAction action,
                CancellationToken ct
            ) =>
            {
                var ownerId = RequireClientId(req);
                if (string.IsNullOrWhiteSpace(ownerId))
                    return TypedResults.BadRequest("Missing X-Client-Id header");

                var contactId = req.Query["contactId"].FirstOrDefault();
                var result = await action.ExecuteAsync(
                    new GetContactSettingsInput(ownerId, contactId),
                    ct
                );
                return TypedResults.Ok(result);
            }
        );

        app.MapPost(
            "/contacts",
            async Task<IResult> (
                HttpRequest req,
                ContactSettingsDto body,
                UpsertContactSettingsApiAction action,
                CancellationToken ct
            ) =>
            {
                var ownerId = RequireClientId(req);
                if (string.IsNullOrWhiteSpace(ownerId))
                    return TypedResults.BadRequest("Missing X-Client-Id header");

                if (string.IsNullOrWhiteSpace(body.ContactId))
                    return TypedResults.BadRequest("contactId is required");

                await action.ExecuteAsync(new UpsertContactSettingsInput(ownerId, body), ct);
                return TypedResults.Ok(new { success = true });
            }
        );

        app.MapPost(
            "/chat/send",
            async Task<Results<BadRequest<string>, Ok<ChatMessageDto>>> (
                HttpRequest req,
                ChatSendRequest request,
                SendChatApiAction action,
                CancellationToken ct
            ) =>
            {
                var clientId = RequireClientId(req);
                if (string.IsNullOrWhiteSpace(clientId))
                    return TypedResults.BadRequest("Missing X-Client-Id header");

                if (
                    string.IsNullOrWhiteSpace(request.Text)
                    || string.IsNullOrWhiteSpace(request.RecipientId)
                )
                    return TypedResults.BadRequest("text and recipientId are required");

                var result = await action.ExecuteAsync(new SendChatInput(clientId, request), ct);
                return TypedResults.Ok(result);
            }
        );

        app.MapGet(
            "/chat/messages",
            async Task<Results<BadRequest<string>, Ok<ChatMessageDto[]>>> (
                HttpRequest req,
                GetChatMessagesApiAction action,
                CancellationToken ct
            ) =>
            {
                var clientId = RequireClientId(req);
                if (string.IsNullOrWhiteSpace(clientId))
                    return TypedResults.BadRequest("Missing X-Client-Id header");

                var peerId = req.Query["peerId"].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(peerId))
                    return TypedResults.BadRequest("peerId query param is required");

                long? sinceId =
                    long.TryParse(req.Query["sinceId"].FirstOrDefault(), out var parsed)
                    && parsed > 0
                        ? parsed
                        : null;

                var result = await action.ExecuteAsync(
                    new GetChatMessagesInput(clientId, peerId, sinceId),
                    ct
                );
                return TypedResults.Ok(result);
            }
        );

        app.MapPost(
            "/rtc-connect",
            (
                RtcConnectAction rtcHandshakeAction,
                RtcConnectionRequest request,
                CancellationToken cancellationToken
            ) => rtcHandshakeAction.ExecuteAsync(request, cancellationToken)
        );
    }
}
