using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using EverModern.Threading;
using EverModern.Threading.Locks;
using WebPhone.Domain;

namespace WebPhone.Backend.Services;

public class OngoingNegotiation(
    TaskCompletionSource<RtcMatchParameter> completionSource,
    WebRtcOffer offer
)
{
    public void Complete(WebRtcAnswer answer)
        => completionSource.TrySetResult(new(offer, answer));
    public void ReplaceOffer(WebRtcOffer offer) => completionSource.TrySetResult(new(offer, null));
    public void Negate() => completionSource.TrySetResult(new(null, null));
    public WebRtcOffer Offer => offer;

    public Task<RtcMatchParameter> WhenCompleted => completionSource.Task;
}

public class RtcNegotiationStore() : LockingDictionary<PeersPair, OngoingNegotiation>(PairsEqualityComparer.Instance) {}

public class LockingDictionary<TKey, TValue>(
    IEqualityComparer<TKey> comparer
) where TKey : notnull
{
    readonly KeyLocker<TKey> _locker = new(comparer);
    readonly ConcurrentDictionary<TKey, TValue> _store = new(comparer);

    public LockingDictionary() : this(EqualityComparer<TKey>.Default) {}
    public LockedDictionaryEntry<TKey, TValue> Acquire(TKey key, Func<TKey, TValue> valueFactory)
    {
        LockedScope? lockedKey = null;
        try
        {
            lockedKey = _locker.Lock(key);
            var value = _store.GetOrAdd(key, valueFactory);
            return new(
                _store,
                key,
                value,
                lockedKey
            );
        }
        catch (Exception ex)
        {
            lockedKey?.Dispose();
            throw;
        }
    }
}

public class LockedDictionaryEntry<TKey, TValue>(
    ConcurrentDictionary<TKey, TValue> store,
    TKey key,
    TValue? dictValue,
    LockedScope locker
) : IDisposable where TKey : notnull
{
    readonly Lock _disposedLock = new();
    bool _disposed;

    T ThrowIfDisposed<T>(T returnedValue)
    {
        if (_disposed)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        return returnedValue;
    }

    public TKey Key => key;
    public TValue? Value
    {
        get => ThrowIfDisposed(dictValue);
        set
        {
            dictValue = ThrowIfDisposed(value);
            store[key] = value;
        }
    }

    public void Remove()
    {
        if (_disposedLock.TryEnter() == false)
            return;

        _disposed = true;
        store.Remove(key, out _);
        locker.Dispose();
    }


    public void Dispose()
    {
        if (_disposedLock.TryEnter() == false)
            return;

        _disposed = true;
        locker.Dispose();
    }
}
