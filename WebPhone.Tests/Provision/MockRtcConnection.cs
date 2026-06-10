using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using Microsoft.AspNetCore.Components;
using WebPhone.Domain;

namespace WebPhone.Tests.Provision;

class EmptyNotifier<T> : INotifier<T>
{
    public Subscription Subscribe(Action<T> handler)
        => new(() => { });
}

record MockRtcConnection(WebRtcOffer Offer, WebRtcAnswer Answer) : IRtcConnection
{
    public INotifier<byte[]> BytesReceived => new EmptyNotifier<byte[]>();

    public INotifier<string> StateChanged => new EmptyNotifier<string>();

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public Task<MediaState> GetMediaStateAsync()
    {
        throw new NotImplementedException();
    }

    public Task<string> GetStateAsync()
    {
        throw new NotImplementedException();
    }

    public Task SetLocalVideoTargetAsync(ElementReference videoElement)
    {
        throw new NotImplementedException();
    }

    public Task SetMediaStateAsync(MediaState mediaState)
    {
        throw new NotImplementedException();
    }

    public Task SetVideoTargetAsync(ElementReference videoElement)
    {
        throw new NotImplementedException();
    }

    public Task<bool> WriteBytesAsync(byte[] bytes)
    {
        throw new NotImplementedException();
    }
}
