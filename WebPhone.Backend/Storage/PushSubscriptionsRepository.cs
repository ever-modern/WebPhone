using Dapper;
using Npgsql;
using WebPhone.Backend.Services;

namespace WebPhone.Backend.Storage;

public sealed record PushSubscriptionKeys(string? P256dh, string? Auth);

public sealed record PushSubscriptionDto(
    string Endpoint,
    PushSubscriptionKeys? Keys,
    string? ContentEncoding
);

public sealed class PushSubscriptionsRepository(DbConnectionResolver connectionResovler)
{
    private static readonly SemaphoreSlim _schemaGate = new(1, 1);
    private static volatile bool _schemaChecked;

    public async Task InsertOrUpdateAsync(
        string clientId,
        PushSubscriptionDto subscription,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await connectionResovler.GetAsync(cancellationToken);

        var endpoint = subscription.Endpoint;
        var p256dh = subscription.Keys?.P256dh;
        var auth = subscription.Keys?.Auth;
        var encoding = subscription.ContentEncoding;

        var sql =
            @"INSERT INTO push_subscriptions
(client_id, endpoint, p256dh, auth, content_encoding, created_at, last_seen)
VALUES 
(@ClientId, @Endpoint, @P256dh, @Auth, @ContentEncoding, now(), now())
ON CONFLICT (client_id) DO UPDATE SET
    p256dh = @P256dh,
    auth = @Auth,
    content_encoding = @ContentEncoding,
    last_seen = now();";

        await connection.ExecuteAsync(
            sql,
            new
            {
                ClientId = clientId,
                Endpoint = endpoint,
                P256dh = p256dh,
                Auth = auth,
                ContentEncoding = encoding,
            }
        );
    }

    public async Task<bool> RemoveByEndpointAsync(
        string endpoint,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await connectionResovler.GetAsync(cancellationToken);

        var sql = "DELETE FROM push_subscriptions WHERE endpoint = @Endpoint;";
        var affected = await connection.ExecuteAsync(sql, new { Endpoint = endpoint });
        return affected > 0;
    }

    public async Task<
        IEnumerable<(string Endpoint, string? P256dh, string? Auth)>
    > GetByClientIdAsync(string clientId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionResovler.GetAsync(cancellationToken);

        var sql =
            "SELECT endpoint, p256dh, auth FROM push_subscriptions WHERE client_id = @ClientId;";
        var rows = await connection.QueryAsync(sql, new { ClientId = clientId });
        return rows.Select(r => ((string)r.endpoint, (string?)r.p256dh, (string?)r.auth));
    }

    public async Task<IEnumerable<(string Endpoint, string? P256dh, string? Auth)>> GetAllAsync(
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await connectionResovler.GetAsync(cancellationToken);

        var sql = "SELECT endpoint, p256dh, auth FROM push_subscriptions;";
        var rows = await connection.QueryAsync(sql);
        return rows.Select(r => ((string)r.endpoint, (string?)r.p256dh, (string?)r.auth));
    }
}
