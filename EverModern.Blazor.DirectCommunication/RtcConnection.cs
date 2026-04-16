using EverModern.Events;
using EverModern.Threading.Channels;
using Microsoft.JSInterop;

namespace EverModern.Blazor.DirectCommunication;

public record MediaState(bool InputEnabled, bool OutputEnabled);

public record WebRtcMediaExchangeState(MediaState Audio, MediaState Video);

public sealed class RtcConnection(
    Action dispose,
    INotifier<string> stateChanged,
    INotifier<byte[]> bytesReceived,
    Func<Task<string>> getState,
    Action<byte[]> writeBytes
) : IDisposable
{
    private bool _disposed;

    public INotifier<string> StateChanged => stateChanged;

    public INotifier<byte[]> BytesReceived => bytesReceived;

    public Task<string> GetStateAsync() => getState();

    public Task SetMediaAsync(WebRtcMediaExchangeState mediaExchangeState)
    {
        ThrowIfDisposed();
        return Task.CompletedTask;
    }

    public Task EnableAudioInputAsync() => Task.CompletedTask;

    public Task DisableAudioInputAsync() => Task.CompletedTask;

    public Task EnableAudioOutputAsync() => Task.CompletedTask;

    public Task DisableAudioOutputAsync() => Task.CompletedTask;

    public Task EnableVideoInputAsync() => Task.CompletedTask;

    public Task DisableVideoInputAsync() => Task.CompletedTask;

    public Task EnableVideoOutputAsync() => Task.CompletedTask;

    public Task DisableVideoOutputAsync() => Task.CompletedTask;

    //public async Task<WebRtcMediaExchangeState> GetMediaExchangeStateAsync()
    //{
    //    ThrowIfDisposed();
    //    var managerReference = GetManagerReference();
    //    return await managerReference.InvokeAsync<WebRtcMediaExchangeState>(
    //        "getMediaExchangeState"
    //    );
    //}

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

    //private async Task InvokeVoidAsync(string methodName, params object[] args)
    //{
    //    ThrowIfDisposed();
    //    var managerReference = GetManagerReference();
    //    await managerReference.InvokeVoidAsync(methodName, args);
    //}

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
