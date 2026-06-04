using Dapper;
using Npgsql;
using WebPhone.Contract;

namespace WebPhone.Backend.Storage;

public sealed class ProfileSettingsRepository(NpgsqlConnection connection)
{
    public async Task<UserSettingsDto> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var sql = """
            SELECT name, notify_calls, notify_messages, notify_from_everyone
            FROM profiles
            WHERE user_id = @UserId;
            """;

        var projectedSql = """
            SELECT
                name                 AS "Name",
                notify_calls         AS "NotifyCalls",
                notify_messages      AS "NotifyMessages",
                notify_from_everyone AS "NotifyFromEveryone"
            FROM profiles
            WHERE user_id = @UserId;
            """;

        var row = await connection.QuerySingleOrDefaultAsync<UserSettingsDto>(
            new CommandDefinition(projectedSql, new { UserId = userId }, cancellationToken: cancellationToken));

        return row ?? new UserSettingsDto("", NotifyCalls: true, NotifyMessages: true, NotifyFromEveryone: false);
    }

    public async Task UpsertAsync(string userId, UserSettingsDto settings, CancellationToken cancellationToken = default)
    {
        var sql = """
            INSERT INTO profiles (user_id, name, notify_calls, notify_messages, notify_from_everyone, updated_at)
            VALUES (@UserId, @Name, @NotifyCalls, @NotifyMessages, @NotifyFromEveryone, NOW())
            ON CONFLICT (user_id)
            DO UPDATE SET
                name = EXCLUDED.name,
                notify_calls = EXCLUDED.notify_calls,
                notify_messages = EXCLUDED.notify_messages,
                notify_from_everyone = EXCLUDED.notify_from_everyone,
                updated_at = NOW();
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                UserId = userId,
                settings.Name,
                settings.NotifyCalls,
                settings.NotifyMessages,
                settings.NotifyFromEveryone
            },
            cancellationToken: cancellationToken));
    }
}
