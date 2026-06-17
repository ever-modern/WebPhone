using System.Collections.Concurrent;

namespace WebPhone.Backend.Services;

using System.Collections.Concurrent;

public sealed class PairMatchLocker
{
    private static readonly Func<PeersPair, SemaphoreSlim> SemaphoreFactory =
        static _ => new SemaphoreSlim(1, 1);

    private readonly ConcurrentDictionary<PeersPair, SemaphoreSlim> _locks =
        new(PairsEqualityComparer.Instance);

    public async Task<IDisposable> LockPairAsync(
        PeersPair pair,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(pair, SemaphoreFactory);

        await semaphore.WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return new Releaser(semaphore);
    }

    public async Task<IDisposable?> TryLockPairAsync(
        PeersPair pair,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(pair, SemaphoreFactory);

        if (!await semaphore.WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        return new Releaser(semaphore);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private int _disposed;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _semaphore.Release();
        }
    }
}