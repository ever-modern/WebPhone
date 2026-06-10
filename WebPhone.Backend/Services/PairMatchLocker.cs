using EverModern.Threading;

namespace WebPhone.Backend.Services;

public class PairMatchLocker
{
    readonly KeyLocker<PeersPair> _locker = new(PairsEqualityComparer.Instance);

    public async Task<LockedScope> LockPairAsync(
        PeersPair pair,
        CancellationToken cancellationToken
    ) => await _locker.LockAsync(pair, cancellationToken);
}
