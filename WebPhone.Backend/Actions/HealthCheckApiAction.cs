using Npgsql;
using WebPhone.Backend.Services;

namespace WebPhone.Backend.Actions;

public sealed record HealthCheckResult(bool Healthy, string Status);

public sealed class HealthCheckApiAction(DbConnectionResolver connectionResolver)
    : ApiActionConcrete<object?, HealthCheckResult>
{
    public override string Route => "/health";

    public override async Task<HealthCheckResult> ExecuteAsync(
        object? input,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            await using var connection = await connectionResolver.GetAsync(cancellationToken);

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
