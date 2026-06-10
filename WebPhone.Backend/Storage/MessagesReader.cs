using System.Text.Json;
using Npgsql;
using WebPhone.Backend.Services;

namespace WebPhone.Backend.Storage;

public sealed class MessagesReader(DbConnectionResolver connectionResovler)
{
    private const int MaxReadResults = 100;

    public async Task<StoredMessage[]> ReadMessagesAsync(
        long? sinceId = null,
        CancellationToken cancellationToken = default
    ) => await ReadMessagesAsync(new MessagesFilter(SinceId: sinceId), cancellationToken);

    public async Task<StoredMessage[]> ReadMessagesAsync(
        MessagesFilter filter,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await connectionResovler.GetAsync(cancellationToken);

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
        command
            .Parameters.Add(
                "SinceId",
                filter.SinceId.HasValue ? (object?)filter.SinceId.Value : null
            )
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

            result.Add(
                new StoredMessage(
                    id,
                    dateTime,
                    type,
                    JsonSerializer.Deserialize<JsonElement>(payloadJson),
                    publisherId,
                    receiverId
                )
            );
        }

        return [.. result];
    }

    /// <summary>
    /// Returns chat messages between two users.
    /// When <paramref name="sinceId"/> is null the most recent <paramref name="limit"/> messages
    /// are returned (newest-first in SQL, then reversed to chronological order for the caller).
    /// When <paramref name="sinceId"/> is provided, all newer messages up to <paramref name="limit"/>
    /// are returned in ascending order — suitable for incremental polling.
    /// </summary>
    public async Task<StoredMessage[]> ReadChatHistoryAsync(
        string userId,
        string peerId,
        long? sinceId = null,
        int limit = 50,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await connectionResovler.GetAsync(cancellationToken);

        string sql;
        if (sinceId is null)
        {
            // Initial load: grab the most recent `limit` rows then flip them to chronological order.
            sql = """
                SELECT id, date_time, type, payload, publisher_id, receiver_id
                FROM (
                    SELECT id, date_time, type, payload, publisher_id, receiver_id
                    FROM messages
                    WHERE type = 'UserChat'
                      AND (
                            (publisher_id = @UserId  AND receiver_id = @PeerId)
                         OR (publisher_id = @PeerId  AND receiver_id = @UserId)
                          )
                    ORDER BY id DESC
                    LIMIT @Limit
                ) sub
                ORDER BY id ASC;
                """;
        }
        else
        {
            // Incremental poll: everything newer than the watermark.
            sql = """
                SELECT id, date_time, type, payload, publisher_id, receiver_id
                FROM messages
                WHERE type = 'UserChat'
                  AND id > @SinceId
                  AND (
                        (publisher_id = @UserId  AND receiver_id = @PeerId)
                     OR (publisher_id = @PeerId  AND receiver_id = @UserId)
                      )
                ORDER BY id ASC
                LIMIT @Limit;
                """;
        }

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("UserId", userId);
        command.Parameters.AddWithValue("PeerId", peerId);
        command.Parameters.AddWithValue("Limit", limit);
        if (sinceId is not null)
            command.Parameters.AddWithValue("SinceId", sinceId.Value);

        var result = new List<StoredMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(
                new StoredMessage(
                    reader.GetInt64(0),
                    DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                    reader.GetString(2),
                    JsonSerializer.Deserialize<JsonElement>(reader.GetString(3)),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)
                )
            );
        }

        return [.. result];
    }
}

public record MessagesFilter(
    string? Type = null,
    string? ReceiverId = null,
    string? PublisherId = null,
    long? SinceId = null,
    IReadOnlyList<string>? ExcludedIds = null
);

public record MessageWriteEntry(
    string Type,
    JsonElement? Payload,
    string PublisherId,
    string? ReceiverId = null,
    DateTime? DateTime = null,
    long? Id = null
);

public sealed record StoredMessage(
    long Id,
    DateTime DateTime,
    string Type,
    JsonElement Payload,
    string PublisherId,
    string? ReceiverId
);
