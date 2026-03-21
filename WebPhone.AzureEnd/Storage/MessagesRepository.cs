using System.Text.Json;
using Dapper;
using Npgsql;
using WebPhone.Registration;

namespace WebPhone.AzureEnd.Storage;

public sealed class MessagesRepository(NpgsqlConnection connection)
{
    public async Task WriteMessageAsync(MessageType messageType, JsonElement payload, CancellationToken cancellationToken = default)
    {
        var sql = """
            INSERT INTO "Messages" ("DateTime", "Type", "Payload")
            VALUES (@DateTime, @MessageType, @Payload::jsonb);
            """;

        var parameters = new
        {
            DateTime = DateTimeOffset.UtcNow,
            MessageType = MessageTypeJsonConverter.ToWireValue(messageType),
            Payload = JsonSerializer.Serialize(payload)
        };

        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    public async Task<StoredMessage[]> ReadMessagesAsync(DateTimeOffset? since = null, CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT "DateTime", "Type", "Payload"
            FROM "Messages"
            """;

        if (since is not null)
        {
            sql += " WHERE \"DateTime\" > @Since";
        }

        sql += " ORDER BY \"DateTime\";";

        var rows = await connection.QueryAsync<MessageRow>(new CommandDefinition(sql, new { Since = since }, cancellationToken: cancellationToken));
        return rows.Select(row => new StoredMessage(new DateTimeOffset(row.DateTime, TimeSpan.Zero), MessageTypeJsonConverter.FromWireValue(row.Type), JsonSerializer.Deserialize<JsonElement>(row.Payload)))
            .ToArray();
    }

    private sealed record MessageRow(DateTime DateTime, string Type, string Payload);
}

public sealed record StoredMessage(DateTimeOffset DateTime, MessageType Type, JsonElement Payload); 