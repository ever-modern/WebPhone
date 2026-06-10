using System.Data;
using EverModern.Chronos;
using EverModern.Threading;
using EverModern.Threading.Queues;
using Npgsql;

namespace WebPhone.Backend.Services;

public class DbConnectionResolver(string connectionString)
{
    readonly DbRateController _rateController = new(
        [new CallConstraint(TimeSpan.FromSeconds(0.1), 3)],
        BetterChronos.Instance
    );

    public async ValueTask<NpgsqlConnection> GetAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(connectionString);

        await _rateController.WhenAllowed(cancellationToken);

        await connection.OpenAsync(cancellationToken);

        return connection;
    }
}

public sealed class DbRateController
{
    private readonly CallConstraint[] _constraints;
    private readonly IChronos _clock;

    private readonly Queue<DateTimeOffset>[] _calls;
    private readonly Lock[] _locks;

    public DbRateController(IEnumerable<CallConstraint> constraints, IChronos clock)
    {
        _constraints = constraints.ToArray();
        _clock = clock;

        _calls = _constraints.Select(c => new Queue<DateTimeOffset>(c.MaxCallsCount)).ToArray();

        _locks = new Lock[_constraints.Length];
        for (int i = 0; i < _locks.Length; i++)
            _locks[i] = new Lock();
    }

    public async ValueTask WhenAllowed(CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var now = _clock.Now;

            if (TryEvaluate(now, out var nextAllowedAt))
            {
                Commit(now);
                return;
            }

            var delay = nextAllowedAt - now;

            if (delay <= TimeSpan.Zero)
            {
                // prevents tight spin in edge timing races
                await Task.Yield();
                continue;
            }

            // HARD GUARD: ensure valid timer input for Chronos
            delay = ClampDelay(delay);

            var target = now + delay;

            // final safety guard against pathological values
            if (target <= now)
            {
                await Task.Yield();
                continue;
            }

            await _clock.WhenComes(target, ct);
        }
    }

    /// <summary>
    /// Pure evaluation only. No mutation.
    /// Computes whether the call is allowed and the next time any constraint frees up.
    /// </summary>
    private bool TryEvaluate(DateTimeOffset now, out DateTimeOffset nextAllowedAt)
    {
        nextAllowedAt = DateTimeOffset.MaxValue;
        var allowed = true;

        for (int i = 0; i < _constraints.Length; i++)
        {
            var constraint = _constraints[i];
            var queue = _calls[i];

            DateTimeOffset localNextAllowedAt = now;

            using (_locks[i].LockScope())
            {
                // remove expired entries
                while (queue.Count > 0 && now - queue.Peek() > constraint.Period)
                {
                    queue.Dequeue();
                }

                if (queue.Count < constraint.MaxCallsCount)
                {
                    continue;
                }

                allowed = false;

                var oldest = queue.Peek();
                localNextAllowedAt = oldest + constraint.Period;
            }

            if (localNextAllowedAt < nextAllowedAt)
                nextAllowedAt = localNextAllowedAt;
        }

        return allowed;
    }

    /// <summary>
    /// Mutation phase only. Called only when request is allowed.
    /// </summary>
    private void Commit(DateTimeOffset now)
    {
        for (int i = 0; i < _constraints.Length; i++)
        {
            var constraint = _constraints[i];
            var queue = _calls[i];

            using (_locks[i].LockScope())
            {
                while (queue.Count > 0 && now - queue.Peek() > constraint.Period)
                {
                    queue.Dequeue();
                }

                queue.Enqueue(now);
            }
        }
    }

    private static TimeSpan ClampDelay(TimeSpan delay)
    {
        // Chronos requires valid timer input range
        if (delay < TimeSpan.Zero)
            return TimeSpan.Zero;

        var max = TimeSpan.FromMilliseconds(int.MaxValue);

        if (delay > max)
            return max;

        return delay;
    }
}

/// <summary>
/// Chronos implementation that uses real system time.
/// </summary>
public sealed class BetterChronos : IChronos
{
    /// <summary>
    /// Gets the shared instance.
    /// </summary>
    public static BetterChronos Instance { get; } = new BetterChronos();

    private BetterChronos()
    {
    }

    /// <inheritdoc />
    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public Task WhenComes(DateTimeOffset targetTime, CancellationToken cancellationToken = default)
    {
        var delay = targetTime - Now;

        // handle past time safely
        if (delay <= TimeSpan.Zero)
            return Task.CompletedTask;

        return Task.Delay(delay, cancellationToken);
    }

    /// <inheritdoc />
    public Task WhenComes(DateTime targetTimeUtc, CancellationToken cancellationToken = default)
        => WhenComes(new DateTimeOffset(targetTimeUtc, TimeSpan.Zero), cancellationToken);

    /// <inheritdoc />
    public Task WhenPasses(TimeSpan time, CancellationToken cancellationToken = default)
    {
        if (time <= TimeSpan.Zero)
            return Task.CompletedTask;

        return Task.Delay(time, cancellationToken);
    }
}