using System.Collections.Concurrent;
using EverModern.Threading;
using EverModern.Threading.Queues;

namespace WebPhone.Services;

class EntityLocker<TId>(IEqualityComparer<TId> equalityComparer)
    where TId : notnull
{
    readonly ConcurrentDictionary<TId, SemaphoreSlim> _locks = new(equalityComparer);

    public async Task<LockedScope> LockAsync(
        TId id,
        CancellationToken cancellationToken = default
    )
    {
        var locker = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        var scopeLocker = await locker.LockScopeAsync(cancellationToken);
        return scopeLocker;
    }
}
