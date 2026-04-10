using Microsoft.AspNetCore.Components;

namespace WebPhone.Components;

/// <summary>
/// Base class for Blazor components that subscribe to services.
/// Subscriptions registered via <see cref="BoundToLifetime{T}"/> are automatically
/// disposed when the component is disposed.
/// </summary>
public abstract class DisposableComponent : ComponentBase, IDisposable, IAsyncDisposable
{
    private event Action? OnDisposed;

    /// <summary>
    /// Registers <paramref name="bound"/> to be disposed together with this component.
    /// Returns <paramref name="bound"/> so it can be used inline.
    /// </summary>
    protected T BoundToLifetime<T>(T bound) where T : IDisposable
    {
        OnDisposed += bound.Dispose;
        return bound;
    }

    public virtual void Dispose()
    {
        OnDisposed?.Invoke();
    }

    public virtual ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
