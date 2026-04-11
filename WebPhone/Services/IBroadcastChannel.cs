using EverModern.Threading.Channels;

namespace WebPhone.Services;

public interface IMessagesChannel : IBroadcastChannel<IncomingMessage, OutgoingMessage> { }

public abstract class BackgroundProcessor : IDisposable
{
    event Action? OnDisposed;

    public T BoundToLifetime<T>(T subscription)
        where T : IDisposable
    {
        OnDisposed += subscription.Dispose;
        return subscription;
    }

    protected virtual void AfterDispose() { }

    public void Dispose()
    {
        OnDisposed?.Invoke();
        AfterDispose();
    }
}
