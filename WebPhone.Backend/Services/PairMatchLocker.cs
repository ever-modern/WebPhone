using System.Collections.Concurrent;

namespace WebPhone.Backend.Services;

public class PairMatchLocker
{
    readonly ConcurrentDictionary<PeersPair, SemaphoreSlim> _locks =
        new(PairsEqualityComparer.Instance);

    SemaphoreSlim GetSemaphore(PeersPair pair) => _locks.GetOrAdd(pair, _ => new(1, 1));

    public async Task<IDisposable> LockPairAsync(
        PeersPair pair,
        CancellationToken cancellationToken
    )
    {
        var semaphore = GetSemaphore(pair);
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    public async Task<IDisposable?> TryLockPairAsync(
        PeersPair pair,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        var semaphore = GetSemaphore(pair);

        var lockAcquired = await semaphore.WaitAsync(timeout, cancellationToken);

        if (!lockAcquired)
            return null;

        return new Releaser(semaphore);
    }

    sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            semaphore.Release();
        }
    }
}
