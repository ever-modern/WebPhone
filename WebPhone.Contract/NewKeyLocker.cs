using System.Collections.Concurrent;

namespace WebPhone.Domain;

public class NewKeyLocker<TKey>(IEqualityComparer<TKey> comparer) where TKey : notnull
{
    private static readonly Func<TKey, SemaphoreSlim> SemaphoreFactory =
        static _ => new SemaphoreSlim(1, 1);

    private readonly ConcurrentDictionary<TKey, SemaphoreSlim> _locks =
        new(comparer);

    public NewKeyLocker() : this(EqualityComparer<TKey>.Default) {}

    public async Task<IDisposable> LockAsync(
        TKey pair,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(pair, SemaphoreFactory);

        await semaphore.WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return new Releaser(semaphore);
    }

    public async Task<IDisposable?> TryLockPairAsync(
        TKey pair,
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