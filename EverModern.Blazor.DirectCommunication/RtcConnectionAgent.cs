using EverModern.Events;
using EverModern.Threading.Channels;
using Microsoft.JSInterop;

namespace EverModern.Blazor.DirectCommunication;

public sealed class RtcConnectionAgent : IAsyncDisposable, IDisposable
{
    private readonly DotNetObjectReference<StateChangedCallback> _stateChangedCallbackReference;
    private readonly DotNetObjectReference<BytesBridgeCallback> _bytesBridgeCallbackReference;
    private readonly EventSource<string> _stateChanged = new();
    private readonly EventSource<byte[]> _bytesReceived = new();
    private IJSObjectReference? _managerReference;
    private int? _bytesBridgeSubscriptionId;
    private bool _disposed;

    internal DotNetObjectReference<StateChangedCallback> StateChangedCallbackReference => _stateChangedCallbackReference;

    public INotifier<string> StateChanged => _stateChanged;

    public INotifier<byte[]> BytesReceived => _bytesReceived;

    internal RtcConnectionAgent()
    {
        _stateChangedCallbackReference = DotNetObjectReference.Create(new StateChangedCallback(state =>
        {
            _stateChanged.Invoke(state);
        }));

        _bytesBridgeCallbackReference = DotNetObjectReference.Create(new BytesBridgeCallback(bytes =>
        {
            _bytesReceived.Invoke(bytes);
        }));
    }

    internal async Task AttachManagerAsync(IJSObjectReference managerReference)
    {
        ThrowIfDisposed();
        _managerReference = managerReference;
        await EnsureBytesBridgeAsync();
    }

    public async Task EnableAudioInputAsync() => await InvokeVoidAsync("enableAudioInput");

    public async Task DisableAudioInputAsync() => await InvokeVoidAsync("disableAudioInput");

    public async Task EnableAudioOutputAsync() => await InvokeVoidAsync("enableAudioOutput");

    public async Task DisableAudioOutputAsync() => await InvokeVoidAsync("disableAudioOutput");

    public async Task EnableVideoInputAsync() => await InvokeVoidAsync("enableVideoInput");

    public async Task DisableVideoInputAsync() => await InvokeVoidAsync("disableVideoInput");

    public async Task EnableVideoOutputAsync() => await InvokeVoidAsync("enableVideoOutput");

    public async Task DisableVideoOutputAsync() => await InvokeVoidAsync("disableVideoOutput");

    public async Task<WebRtcMediaExchangeState> GetMediaExchangeStateAsync()
    {
        ThrowIfDisposed();
        var managerReference = GetManagerReference();
        return await managerReference.InvokeAsync<WebRtcMediaExchangeState>("getMediaExchangeState");
    }

    public async Task WriteBytesAsync(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        await InvokeVoidAsync("writeBytes", bytes);
    }

    public async Task<Subscription> SubscribeBytesAsync(Action<byte[]> onBytesReceived)
    {
        ArgumentNullException.ThrowIfNull(onBytesReceived);

        ThrowIfDisposed();
        await EnsureBytesBridgeAsync();
        return _bytesReceived.Subscribe(onBytesReceived);
    }

    public async Task<WebRtcAnswer> GetLocalAnswerAsync()
    {
        ThrowIfDisposed();
        var managerReference = GetManagerReference();
        return await managerReference.InvokeAsync<WebRtcAnswer>("getLocalAnswer");
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

        if (_managerReference is not null)
        {
            if (_bytesBridgeSubscriptionId.HasValue)
            {
                try
                {
                    await _managerReference.InvokeVoidAsync("unsubscribeBytes", _bytesBridgeSubscriptionId.Value);
                }
                catch (JSDisconnectedException)
                {
                }
            }

            try
            {
                await _managerReference.InvokeVoidAsync("close");
            }
            catch (JSDisconnectedException)
            {
            }

            try
            {
                await _managerReference.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _stateChangedCallbackReference.Dispose();
        _bytesBridgeCallbackReference.Dispose();
    }

    private async Task EnsureBytesBridgeAsync()
    {
        if (_bytesBridgeSubscriptionId.HasValue)
        {
            return;
        }

        var managerReference = GetManagerReference();
        _bytesBridgeSubscriptionId = await managerReference.InvokeAsync<int>("subscribeBytes", _bytesBridgeCallbackReference);
    }

    private async Task InvokeVoidAsync(string methodName, params object[] args)
    {
        ThrowIfDisposed();
        var managerReference = GetManagerReference();
        await managerReference.InvokeVoidAsync(methodName, args);
    }

    private IJSObjectReference GetManagerReference() =>
        _managerReference ?? throw new InvalidOperationException("The RTC connection agent has not been initialized.");

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RtcConnectionAgent));
        }
    }

    internal sealed class StateChangedCallback(Action<string> callback)
    {
        [JSInvokable]
        public Task OnStateChanged(string state)
        {
            callback(state);
            return Task.CompletedTask;
        }
    }

    internal sealed class BytesBridgeCallback(Action<byte[]> callback)
    {
        [JSInvokable]
        public Task OnBytesReceived(byte[] bytes)
        {
            callback(bytes);
            return Task.CompletedTask;
        }
    }
}
