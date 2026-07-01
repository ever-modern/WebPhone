using EverModern.Threading.Locks;
using WebPhone.Channels;
using WebPhone.Data;

namespace WebPhone.Background;

public sealed class AppStarter(
    ProfileStore profileStore,
    BackendMessagesChannel backendMessagesChannel,
    ContactsRepository contactsRepository,
    IncomingConnectionsHandler incomingConnectionsHandler,
    PresenceAnnouncer presenceAnnouncer,
    ContactsDispatcher contactsDispatcher,
    IBackendClient  backendClient
)
{
    readonly SemaphoreSlim _startLock = new(
        1,
        1
    );
    bool _started;

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
            return;

        using var __ = await _startLock.LockScopeAsync(
            cancellationToken
        );

        if (_started)
            return;

        await profileStore.InitializeAsync(
            cancellationToken
        );
        await contactsRepository.InitializeAsync(
            cancellationToken
        );
        contactsRepository.StartTracking();
        

        var hub = await backendClient.OpenHubConnectionAsync(cancellationToken);

        await backendMessagesChannel.StartAsync(hub);
        
        await incomingConnectionsHandler.StartReadingAsync(backendMessagesChannel);
        
        await presenceAnnouncer.StartAsync();
        contactsDispatcher.Started();

        _started = true;
    }
}
