using EverModern.Events;

namespace EverModern.Blazor.DirectCommunication;

public record MediaPartState(bool InputEnabled, bool OutputEnabled);

public record MediaState(MediaPartState Audio, MediaPartState Video);

public sealed class RtcConnection(
    Action dispose,
    INotifier<string> stateChanged,
    INotifier<byte[]> bytesReceived,
    Func<Task<string>> getState,
    Action<byte[]> writeBytes,
    Func<Task<MediaState>> getMediaState,
    Func<MediaState, Task> setMediaState,
    Func<Microsoft.AspNetCore.Components.ElementReference, Task> setVideoTarget,
    Func<Microsoft.AspNetCore.Components.ElementReference, Task> setLocalVideoTarget
) : IDisposable
{
    private bool _disposed;

    public INotifier<string> StateChanged => stateChanged;

    public INotifier<byte[]> BytesReceived => bytesReceived;

    public Task<string> GetStateAsync() => getState();

    public async Task<MediaState> GetMediaStateAsync()
    {
        ThrowIfDisposed();
        return await getMediaState();
    }

    public async Task SetMediaStateAsync(MediaState mediaState)
    {
        ArgumentNullException.ThrowIfNull(mediaState);
        ThrowIfDisposed();
        await setMediaState(mediaState);
    }

    public async Task SetVideoTargetAsync(Microsoft.AspNetCore.Components.ElementReference videoElement)
    {
        ThrowIfDisposed();
        await setVideoTarget(videoElement);
    }

    public async Task SetLocalVideoTargetAsync(Microsoft.AspNetCore.Components.ElementReference videoElement)
    {
        ThrowIfDisposed();
        await setLocalVideoTarget(videoElement);
    }

    public async Task WriteBytesAsync(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ThrowIfDisposed();
        writeBytes(bytes);
    }

    public void Dispose()
    {
        _ = DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        dispose();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
