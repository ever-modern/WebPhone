using System.Threading;
using EverModern.Threading;
using EverModern.Threading.Queues;
using WebPhone.Services.Channels;
using WebPhone.Services.Data;

namespace WebPhone.Services.Background;

public sealed class AppStarter(
    ProfileStore profileStore,
    BackendMessagesChannel backendMessagesChannel,
    ContactsRepository contactsRepository,
    IncomingConnectionsHandler incomingConnectionsHandler,
    PresenceAnnouncer presenceAnnouncer,
    ContactsDispatcher contactsDispatcher
)
{
    readonly SemaphoreSlim _startLock = new(1, 1);
    volatile bool _started;

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
            return;

        using var __ = await _startLock.LockScopeAsync(cancellationToken);

        if (_started)
            return;

        await profileStore.InitializeAsync(cancellationToken);
        _ = backendMessagesChannel.Start();
        await contactsRepository.InitializeAsync(cancellationToken);
        contactsRepository.StartTracking();
        incomingConnectionsHandler.Start();
        await presenceAnnouncer.StartAsync();
        await contactsDispatcher.StartAsync(cancellationToken);

        _started = true;
    }
}
