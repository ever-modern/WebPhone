using System.Collections.Concurrent;
using EverModern.Events;
using EverModern.Threading;

namespace WebPhone.Services;

/// <summary>
/// Provides asynchronous per-key locking.
/// </summary>
using System.Collections.Concurrent;
using EverModern.Events;

public sealed class NewKeyLocker<TKey>(
    IEqualityComparer<TKey> comparer
) : IDisposable
    where TKey : notnull
{
    readonly ConcurrentDictionary<TKey, Entry> _locks = new(comparer);
    int _disposed;

    public NewKeyLocker() : this(EqualityComparer<TKey>.Default) {}

    public async ValueTask<Subscription> LockAsync(
        TKey key,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this
        );

        var entry = _locks.GetOrAdd(key, _ => new Entry());

        Interlocked.Increment(ref entry.RefCount);

        try
        {
            await entry.Semaphore
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            ReleaseReference(key, entry);
            throw;
        }

        return new(sub =>
            {
                using var _ = sub;

                entry.Semaphore.Release();
                ReleaseReference(key, entry);
            }
        );
    }

    private void ReleaseReference(TKey key, Entry entry)
    {
        if (Interlocked.Decrement(ref entry.RefCount) != 0)
            return;

        _locks.TryRemove(new KeyValuePair<TKey, Entry>(key, entry));

        if (Volatile.Read(ref _disposed) != 0)
        {
            entry.Semaphore.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var (_, entry) in _locks)
        {
            if (Volatile.Read(ref entry.RefCount) == 0)
            {
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }
}
