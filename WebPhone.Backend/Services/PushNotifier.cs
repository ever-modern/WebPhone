using System.Threading;
using System.Threading.Tasks;
using WebPhone.Backend.Storage;
using Microsoft.Extensions.Logging;
using WebPhone.Contract;

namespace WebPhone.Backend.Services;

public class PushNotifier
{
    private readonly PushSubscriptionsRepository _subscriptions;
    private readonly ILogger<PushNotifier> _logger;

    public PushNotifier(PushSubscriptionsRepository subscriptions, ILogger<PushNotifier> logger)
    {
        _subscriptions = subscriptions;
        _logger = logger;
    }

    public async Task<bool> PushToClientAsync(string clientId, string payload, CancellationToken cancellationToken = default)
    {
        // Use compile-time VAPID keys
        var publicKey = VapidKeys.Public;
        var privateKey = VapidKeys.Private;

        // Get all subscriptions for the client
        var subs = await _subscriptions.GetByClientIdAsync(clientId, cancellationToken);
        if (!subs.Any())
        {
            _logger.LogWarning("No push subscriptions for client {ClientId}", clientId);
            return false;
        }

        bool anySuccess = false;
        using var webPushClient = new WebPush.WebPushClient();
        foreach (var (endpoint, p256dh, auth) in subs)
        {
            try
            {
                var subscription = new WebPush.PushSubscription(endpoint, p256dh, auth);
                var vapidDetails = new WebPush.VapidDetails("mailto:admin@example.com", publicKey, privateKey);
                await webPushClient.SendNotificationAsync(subscription, payload, vapidDetails, cancellationToken);
                anySuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push to endpoint {Endpoint}", endpoint);
                if (ex is WebPush.WebPushException wpe)
                {
                    var statusCode = (int)wpe.StatusCode;
                    if (statusCode is 404 or 410)
                        await _subscriptions.RemoveByEndpointAsync(endpoint, cancellationToken);
                }
            }
        }
        return anySuccess;
    }
}
