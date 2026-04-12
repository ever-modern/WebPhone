using System.Text.Json;
using Dapper;
using Npgsql;
using WebPhone.Contract;

namespace WebPhone.AzureEnd.Storage;

public sealed class MessagesRepository(NpgsqlConnection connection)
{
    private const int DefaultWriteBatchSize = 100;
    private const int MaxReadResults = 100;

    public async Task<DateTime> WriteMessageAsync(
        string messageType,
        JsonElement payload,
        string publisherId = "",
        string? receiverId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await WriteMessagesAsync([new MessageWriteEntry(messageType, payload, publisherId, receiverId)], cancellationToken: cancellationToken);
        return result;
    }

    public async Task<DateTime> WriteMessagesAsync(
        IReadOnlyList<MessageWriteEntry> messages,
        int batchSize = DefaultWriteBatchSize,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
        {
            return DateTime.UtcNow;
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
                var idParameter = $"Id{parameterSuffix}";
                var dateTimeParameter = $"DateTime{parameterSuffix}";
                var typeParameter = $"Type{parameterSuffix}";
                var payloadParameter = $"Payload{parameterSuffix}";
                var publisherIdParameter = $"PublisherId{parameterSuffix}";
                var receiverIdParameter = $"ReceiverId{parameterSuffix}";

                values.Add($"(@{idParameter}, @{dateTimeParameter}, @{typeParameter}, @{payloadParameter}::jsonb, @{publisherIdParameter}, @{receiverIdParameter})");
                parameters.Add(idParameter, CommonIdsGenerator.NewId());
                parameters.Add(dateTimeParameter, DateTime.SpecifyKind(message.DateTime ?? DateTime.UtcNow, DateTimeKind.Unspecified));
                parameters.Add(typeParameter, message.Type as object ?? DBNull.Value);
                parameters.Add(payloadParameter, message.Payload is null ? "{}" : JsonSerializer.Serialize(message.Payload));
                parameters.Add(publisherIdParameter, message.PublisherId ?? string.Empty);
                parameters.Add(receiverIdParameter, message.ReceiverId);
            }

            var sql = $"""
                INSERT INTO messages (id, date_time, type, payload, publisher_id, receiver_id)
                VALUES {string.Join(", ", values)};
                """;

            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        }

        return DateTime.UtcNow;
    }

    public async Task<StoredMessage[]> ReadMessagesAsync(long? sinceId = null, CancellationToken cancellationToken = default)
        => await ReadMessagesAsync(new MessagesFilter(SinceId: sinceId), cancellationToken);

    public async Task<StoredMessage[]> ReadMessagesAsync(MessagesFilter filter, CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT id, date_time, type, payload, publisher_id, receiver_id
            FROM messages
            WHERE (@SinceId IS NULL OR id > @SinceId)
              AND (@Type IS NULL OR type = @Type)
              AND (@PublisherId IS NULL OR publisher_id = @PublisherId)
              AND (@ExcludedIds IS NULL OR NOT (publisher_id = ANY(@ExcludedIds)))
              AND (
                    @ReceiverId IS NULL
                    OR receiver_id = @ReceiverId
                    OR receiver_id IS NULL
                  )
            ORDER BY id
            LIMIT @Limit;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters
            .Add("SinceId", filter.SinceId.HasValue ? (object?)filter.SinceId.Value : null)
            .Add("Type", filter.Type)
            .Add("PublisherId", filter.PublisherId)
            .Add("ExcludedIds", filter.ExcludedIds is null ? null : filter.ExcludedIds.ToArray())
            .Add("ReceiverId", filter.ReceiverId)
            .Add("Limit", MaxReadResults);

        var result = new List<StoredMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var dateTime = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);

            var type = reader.GetString(2);
            var payloadJson = reader.GetString(3);
            var publisherId = reader.GetString(4);
            var receiverId = reader.IsDBNull(5) ? null : reader.GetString(5);

            result.Add(new StoredMessage(
                id,
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
    long? SinceId = null,
    IReadOnlyList<string>? ExcludedIds = null);

public record MessageWriteEntry(
    string Type,
    JsonElement? Payload,
    string PublisherId,
    string? ReceiverId = null,
    DateTime? DateTime = null);

public sealed record StoredMessage(
    long Id, DateTime DateTime, string Type, JsonElement Payload, string PublisherId, string? ReceiverId);
