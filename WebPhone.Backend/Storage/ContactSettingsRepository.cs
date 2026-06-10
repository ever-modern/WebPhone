using Dapper;
using Npgsql;
using WebPhone.Domain;

namespace WebPhone.Backend.Storage;

public sealed class ContactSettingsRepository(NpgsqlConnection connection)
{
    public async Task<ContactSettingsDto> GetAsync(
        string ownerId,
        string contactId,
        CancellationToken cancellationToken = default)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var sql = """
            SELECT
                owner_id        AS "OwnerId",
                contact_id      AS "ContactId",
                is_favourite    AS "IsFavourite",
                notify_calls    AS "NotifyCalls",
                notify_messages AS "NotifyMessages",
                nickname        AS "Nickname"
            FROM contacts
            WHERE owner_id = @OwnerId AND contact_id = @ContactId;
            """;

        var row = await connection.QuerySingleOrDefaultAsync<ContactSettingsDto>(
            new CommandDefinition(sql, new { OwnerId = ownerId, ContactId = contactId }, cancellationToken: cancellationToken));

        return row ?? new ContactSettingsDto(ownerId, contactId, false, true, true, null);
    }

    public async Task<ContactSettingsDto[]> GetByOwnerAsync(string ownerId, CancellationToken cancellationToken = default)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var sql = """
            SELECT
                owner_id        AS "OwnerId",
                contact_id      AS "ContactId",
                is_favourite    AS "IsFavourite",
                notify_calls    AS "NotifyCalls",
                notify_messages AS "NotifyMessages",
                nickname        AS "Nickname"
            FROM contacts
            WHERE owner_id = @OwnerId;
            """;

        var rows = await connection.QueryAsync<ContactSettingsDto>(
            new CommandDefinition(sql, new { OwnerId = ownerId }, cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task UpsertAsync(ContactSettingsDto settings, CancellationToken cancellationToken = default)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);

        var sql = """
            INSERT INTO contacts (owner_id, contact_id, is_favourite, notify_calls, notify_messages, nickname, updated_at)
            VALUES (@OwnerId, @ContactId, @IsFavourite, @NotifyCalls, @NotifyMessages, @Nickname, NOW())
            ON CONFLICT (owner_id, contact_id)
            DO UPDATE SET
                is_favourite = EXCLUDED.is_favourite,
                notify_calls = EXCLUDED.notify_calls,
                notify_messages = EXCLUDED.notify_messages,
                nickname = EXCLUDED.nickname,
                updated_at = NOW();
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            settings.OwnerId,
            settings.ContactId,
            settings.IsFavourite,
            settings.NotifyCalls,
            settings.NotifyMessages,
            settings.Nickname
        }, cancellationToken: cancellationToken));
    }
}
