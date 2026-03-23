using System.Text.Json;
using Dapper;
using Npgsql;
using WebPhone.Registration;

namespace WebPhone.AzureEnd.Storage;

public sealed class MessagesRepository(NpgsqlConnection connection)
{
    private const int DefaultWriteBatchSize = 100;
    private const int MaxReadResults = 100;

    public async Task WriteMessageAsync(
        string messageType,
        JsonElement payload,
        string publisherId = "",
        string? receiverId = null,
        CancellationToken cancellationToken = default)
    {
        await WriteMessagesAsync([new MessageWriteEntry(messageType, payload, publisherId, receiverId)], cancellationToken: cancellationToken);
    }

    public async Task WriteMessagesAsync(
        IReadOnlyList<MessageWriteEntry> messages,
        int batchSize = DefaultWriteBatchSize,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var normalizedBatchSize = Math.Max(1, batchSize);

        for (var start = 0; start < messages.Count; start += normalizedBatchSize)
        {
            var count = Math.Min(normalizedBatchSize, messages.Count - start);
            var parameters = new DynamicParameters();
            var values = new List<string>(count);

            for (var index = 0; index < count; index++)
            {
                var message = messages[start + index];
                var parameterSuffix = index.ToString();
                var dateTimeParameter = $"DateTime{parameterSuffix}";
                var typeParameter = $"Type{parameterSuffix}";
                var payloadParameter = $"Payload{parameterSuffix}";
                var publisherIdParameter = $"PublisherId{parameterSuffix}";
                var receiverIdParameter = $"ReceiverId{parameterSuffix}";

                values.Add($"(@{dateTimeParameter}, @{typeParameter}, @{payloadParameter}::jsonb, @{publisherIdParameter}, @{receiverIdParameter})");
                parameters.Add(dateTimeParameter, message.DateTime ?? DateTimeOffset.UtcNow);
                parameters.Add(typeParameter, message.Type as object ?? DBNull.Value);
                parameters.Add(payloadParameter, message.Payload is null ? "{}" : JsonSerializer.Serialize(message.Payload));
                parameters.Add(publisherIdParameter, message.PublisherId ?? string.Empty);
                parameters.Add(receiverIdParameter, message.ReceiverId);
            }

            var sql = $"""
                INSERT INTO "Messages" ("DateTime", "Type", "Payload", "PublisherId", "ReceiverId")
                VALUES {string.Join(", ", values)};
                """;

            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        }
    }

    public async Task<StoredMessage[]> ReadMessagesAsync(DateTimeOffset? since = null, CancellationToken cancellationToken = default)
        => await ReadMessagesAsync(new MessagesFilter(Since: since), cancellationToken);

    public async Task<StoredMessage[]> ReadMessagesAsync(MessagesFilter filter, CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT "DateTime", "Type", "Payload", "PublisherId", "ReceiverId"
            FROM "Messages"
            WHERE (@Since IS NULL OR "DateTime" > @Since)
              AND (@Type IS NULL OR "Type" = @Type)
              AND (@PublisherId IS NULL OR "PublisherId" = @PublisherId)
              AND (@ExcludedIds IS NULL OR NOT ("PublisherId" = ANY(@ExcludedIds)))
              AND (
                    @ReceiverId IS NULL
                    OR "ReceiverId" = @ReceiverId
                    OR "ReceiverId" IS NULL
                  )
            ORDER BY "DateTime"
            LIMIT @Limit;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters
            .Add("Since", filter.Since)
            .Add("Type", filter.Type)
            .Add("PublisherId", filter.PublisherId)
            .Add("ExcludedIds", filter.ExcludedIds is null ? null : filter.ExcludedIds.ToArray())
            .Add("ReceiverId", filter.ReceiverId)
            .Add("Limit", MaxReadResults);

        var result = new List<StoredMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rawDateTime = reader.GetValue(0);
            var dateTime = rawDateTime switch
            {
                DateTimeOffset dto => dto,
                DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
                _ => DateTimeOffset.Parse(rawDateTime.ToString()!)
            };

            var type = reader.GetString(1);
            var payloadJson = reader.GetString(2);
            var publisherId = reader.GetString(3);
            var receiverId = reader.IsDBNull(4) ? null : reader.GetString(4);

            result.Add(new StoredMessage(
                dateTime,
                type,
                JsonSerializer.Deserialize<JsonElement>(payloadJson),
                publisherId,
                receiverId));
        }

        return [.. result];
    }
}

public record MessagesFilter(
    string? Type = null,
    string? ReceiverId = null,
    string? PublisherId = null,
    DateTimeOffset? Since = null,
    IReadOnlyList<string>? ExcludedIds = null);

public record MessageWriteEntry(
    string Type,
    JsonElement? Payload,
    string PublisherId,
    string? ReceiverId = null,
    DateTimeOffset? DateTime = null);

public sealed record StoredMessage(
    DateTimeOffset DateTime, string Type, JsonElement Payload, string PublisherId, string? ReceiverId);
