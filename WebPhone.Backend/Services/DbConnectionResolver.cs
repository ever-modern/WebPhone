using System.Data;
using EverModern.Chronos;
using EverModern.Threading;
using EverModern.Threading.Locks;
using EverModern.Threading.Queues;
using Npgsql;

namespace WebPhone.Backend.Services;

public class DbConnectionResolver(string connectionString)
{
    readonly RateController _rateController = new(
        [new CallConstraint(TimeSpan.FromSeconds(0.1), 3)],
        RealtimeChronos.Instance
    );

    public async ValueTask<NpgsqlConnection> GetAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(connectionString);

        await _rateController.WhenAllowed(cancellationToken);

        await connection.OpenAsync(cancellationToken);

        return connection;
    }
}
