using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace EverModern.Blazor.DirectCommunication;



public sealed class WebRtcService(IJSRuntime jsRuntime) : IAsyncDisposable
{
    private readonly IJSRuntime jsRuntime = jsRuntime;
    private DotNetObjectReference<WebRtcService>? dotNetReference;

    public event EventHandler<WebRtcIceCandidateEventArgs>? IceCandidateReceived;

    public event EventHandler<WebRtcConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public event EventHandler<WebRtcDataChannelStateChangedEventArgs>? DataChannelStateChanged;

    public event EventHandler<WebRtcDataMessageEventArgs>? DataMessageReceived;

    public event EventHandler<WebRtcRemoteStreamEventArgs>? RemoteStreamAvailable;

    public async ValueTask InitializeAsync(string connectionId, IEnumerable<WebRtcIceServer>? iceServers = null)
    {
        dotNetReference ??= DotNetObjectReference.Create(this);
        await jsRuntime.InvokeVoidAsync("webrtcInterop.createConnection", connectionId, dotNetReference, iceServers);
    }

    public async ValueTask<IJSObjectReference> StartLocalStreamAsync(string connectionId, object? constraints = null)
    {
        return await jsRuntime.InvokeAsync<IJSObjectReference>("webrtcInterop.startLocalStream", connectionId, constraints);
    }

    public async ValueTask AddLocalTracksAsync(string connectionId)
    {
        await jsRuntime.InvokeVoidAsync("webrtcInterop.addLocalTracks", connectionId);
    }

    public async ValueTask CreateDataChannelAsync(string connectionId, string label, WebRtcDataChannelOptions? options = null)
    {
        await jsRuntime.InvokeVoidAsync("webrtcInterop.createDataChannel", connectionId, label, options);
    }

    public async ValueTask<WebRtcSessionDescription> CreateOfferAsync(string connectionId)
    {
        return await jsRuntime.InvokeAsync<WebRtcSessionDescription>("webrtcInterop.createOffer", connectionId);
    }

    public async ValueTask<WebRtcSessionDescription> CreateAnswerAsync(string connectionId)
    {
        return await jsRuntime.InvokeAsync<WebRtcSessionDescription>("webrtcInterop.createAnswer", connectionId);
    }

    public async ValueTask SetRemoteDescriptionAsync(string connectionId, WebRtcSessionDescription description)
    {
        await jsRuntime.InvokeVoidAsync("webrtcInterop.setRemoteDescription", connectionId, description);
    }

    public async ValueTask AddIceCandidateAsync(string connectionId, WebRtcIceCandidate candidate)
    {
        await jsRuntime.InvokeVoidAsync("webrtcInterop.addIceCandidate", connectionId, candidate);
    }

    public async ValueTask SendMessageAsync(string connectionId, string message)
    {
        await jsRuntime.InvokeVoidAsync("webrtcInterop.sendData", connectionId, message);
    }

    public async ValueTask CloseAsync(string connectionId)
    {
        await jsRuntime.InvokeVoidAsync("webrtcInterop.closeConnection", connectionId);
    }

    public async ValueTask AttachRemoteAudioAsync(string connectionId, ElementReference audioElement)
    {
        await jsRuntime.InvokeVoidAsync("webrtcInterop.attachRemoteStream", connectionId, audioElement);
    }

    [JSInvokable]
    public Task OnIceCandidate(string connectionId, WebRtcIceCandidate candidate)
    {
        IceCandidateReceived?.Invoke(this, new WebRtcIceCandidateEventArgs(connectionId, candidate));
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnConnectionStateChanged(string connectionId, string state)
    {
        ConnectionStateChanged?.Invoke(this, new WebRtcConnectionStateChangedEventArgs(connectionId, state));
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnDataChannelStateChanged(string connectionId, string state)
    {
        DataChannelStateChanged?.Invoke(this, new WebRtcDataChannelStateChangedEventArgs(connectionId, state));
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnDataChannelMessage(string connectionId, string message)
    {
        DataMessageReceived?.Invoke(this, new WebRtcDataMessageEventArgs(connectionId, message));
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnRemoteStream(string connectionId)
    {
        RemoteStreamAvailable?.Invoke(this, new WebRtcRemoteStreamEventArgs(connectionId));
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        dotNetReference?.Dispose();
    }
}
