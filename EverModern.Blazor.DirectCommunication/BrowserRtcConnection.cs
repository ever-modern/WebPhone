using EverModern.Events;

namespace EverModern.Blazor.DirectCommunication;

public record MediaPartState(
    bool InputEnabled,
    bool OutputEnabled
);

public record MediaState(
    MediaPartState Audio,
    MediaPartState Video
);

public abstract class DelegatedRtcConnection(
    Func<string> getId,
    Func<ValueTask> dispose,
    IValueNotifier<RtcConnectionState> state,
    INotifier<byte[]> bytesReceived,
    Func<byte[], ValueTask<bool>> writeBytes,
    Func<Task<MediaState>> getMediaState,
    Func<MediaState, Task> setMediaState,
    Func<Microsoft.AspNetCore.Components.ElementReference, Task> setVideoTarget,
    Func<Microsoft.AspNetCore.Components.ElementReference, Task> setLocalVideoTarget
) : IRtcConnection
{
    bool _disposed;

    public string Id => getId();

    public IValueNotifier<RtcConnectionState> State => state;

    public INotifier<byte[]> BytesReceived => bytesReceived;

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

    public async ValueTask<bool> WriteBytesAsync(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ThrowIfDisposed();
        return await writeBytes(bytes);
    }

    public void Dispose() { _ = DisposeAsync(); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await dispose();
    }

    void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(_disposed, this); }
}

public class BrowserRtcConnection(
    Func<string> getId,
    Func<ValueTask> dispose,
    IValueNotifier<RtcConnectionState> state,
    INotifier<byte[]> bytesReceived,
    Func<byte[], ValueTask<bool>> writeBytes,
    Func<Task<MediaState>> getMediaState,
    Func<MediaState, Task> setMediaState,
    Func<Microsoft.AspNetCore.Components.ElementReference, Task> setVideoTarget,
    Func<Microsoft.AspNetCore.Components.ElementReference, Task> setLocalVideoTarget
) : DelegatedRtcConnection(
    getId,
    dispose,
    state,
    bytesReceived,
    writeBytes,
    getMediaState,
    setMediaState,
    setVideoTarget,
    setLocalVideoTarget
);
