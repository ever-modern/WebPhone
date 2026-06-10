using System.Text.Json;
using Dapper;
using EverModern.Threading;
using EverModern.Threading.Channels;
using WebPhone.Backend.Services;
using WebPhone.Domain;

namespace WebPhone.Backend.Storage;

public class MessagesWriter(DbConnectionResolver connectionResovler) : IDisposable
{
    private const int DefaultWriteBatchSize = 100;

    readonly BroadcastChannel<MessageWriteEntry> _channel = new();
    readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Dispose();
    }

    public async ValueTask EnqueueAsync(
        IEnumerable<MessageWriteEntry> entries,
        CancellationToken cancellationToken = default
    )
    {
        foreach (var entry in entries)
        {
            await _channel.WriteAsync(entry, cancellationToken);
        }
    }

    public ValueTask EnqueueAsync(
        string messageType,
        JsonElement payload,
        string publisherId = "",
        string? receiverId = null,
        CancellationToken cancellationToken = default
    ) =>
        EnqueueAsync(
            [new MessageWriteEntry(messageType, payload, publisherId, receiverId)],
            cancellationToken: cancellationToken
        );

    public MessagesWriter Start()
    {
        List<MessageWriteEntry> batch = new();

        Lock batchLock = new();

        _ = Task.Run(async () =>
        {
            await using var connection = await connectionResovler.GetAsync(_cts.Token);
            while (_cts.IsCancellationRequested == false)
            {
                await Task.Delay(500);

                MessageWriteEntry[] toWrite;
                using (batchLock.LockScope())
                {
                    toWrite = batch.ToArray();
                    batch = [];
                }
                try
                {
                    await WriteMessagesAsync(toWrite, cancellationToken: _cts.Token);
                }
                catch (Exception ex) { }
            }
        });

        var __ = Task.Run(async () =>
        {
            using var reader = _channel.Subscribe(_ => true);
            await foreach (var entry in reader.ReadAllAsync(_cts.Token))
            {
                using var _ = batchLock.LockScope();
                batch.Add(entry);
            }
        });

        return this;
    }

    async Task WriteMessagesAsync(
        IReadOnlyList<MessageWriteEntry> messages,
        int batchSize = DefaultWriteBatchSize,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await connectionResovler.GetAsync(cancellationToken);

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
                var idParameter = $"Id{parameterSuffix}";
                var dateTimeParameter = $"DateTime{parameterSuffix}";
                var typeParameter = $"Type{parameterSuffix}";
                var payloadParameter = $"Payload{parameterSuffix}";
                var publisherIdParameter = $"PublisherId{parameterSuffix}";
                var receiverIdParameter = $"ReceiverId{parameterSuffix}";

                values.Add(
                    $"(@{idParameter}, @{dateTimeParameter}, @{typeParameter}, @{payloadParameter}::jsonb, @{publisherIdParameter}, @{receiverIdParameter})"
                );
                parameters.Add(idParameter, message.Id ?? CommonIdsGenerator.NewId());
                parameters.Add(
                    dateTimeParameter,
                    DateTime.SpecifyKind(
                        message.DateTime ?? DateTime.UtcNow,
                        DateTimeKind.Unspecified
                    )
                );
                parameters.Add(typeParameter, message.Type as object ?? DBNull.Value);
                parameters.Add(
                    payloadParameter,
                    message.Payload is null ? "{}" : JsonSerializer.Serialize(message.Payload)
                );
                parameters.Add(publisherIdParameter, message.PublisherId ?? string.Empty);
                parameters.Add(receiverIdParameter, message.ReceiverId);
            }

            var sql = $"""
                INSERT INTO messages (id, date_time, type, payload, publisher_id, receiver_id)
                VALUES {string.Join(", ", values)};
                """;

            await connection.ExecuteAsync(
                new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)
            );
        }
    }
}
