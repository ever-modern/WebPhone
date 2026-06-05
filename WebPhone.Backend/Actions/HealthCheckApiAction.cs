using Npgsql;

namespace WebPhone.Backend.Actions;

public sealed record HealthCheckResult(bool Healthy, string Status);

public sealed class HealthCheckApiAction(NpgsqlConnection connection)
    : ApiActionConcrete<object?, HealthCheckResult>
{
    public override string Route => "/health";

    public override async Task<HealthCheckResult> ExecuteAsync(
        object? input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
                await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";

            var result = await command.ExecuteScalarAsync(cancellationToken);
            var isHealthy = result is 1 or 1L;

            return new HealthCheckResult(isHealthy, isHealthy ? "ok" : "db-check-failed");
        }
        catch
        {
            return new HealthCheckResult(false, "db-unreachable");
        }
    }
}
