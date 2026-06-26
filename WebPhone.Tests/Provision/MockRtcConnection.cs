using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using Microsoft.AspNetCore.Components;
using WebPhone.Domain;

namespace WebPhone.Tests.Provision;

class EmptyNotifier<T> : INotifier<T>
{
    public Subscription Subscribe(Action<T> handler)
        => new(() => {});
}

class MockRtcConnection : IRtcConnection
{
    static readonly List<MockRtcConnection> _existingConnections = new();

    readonly ObservedValue<RtcConnectionState> _state = new(RtcConnectionState.New);
    public MockRtcConnection(MockRtcConnector owner, WebRtcOffer offer, WebRtcAnswer answer)
    {
        Owner = owner;
        Offer = offer;
        Answer = answer;

        Id = RtcMatchParameters.ComputeNegotiationId(Offer, Answer).ToString();
        _existingConnections.Add(this);
    }

    public string Id { get; }

    public INotifier<byte[]> BytesReceived => new EmptyNotifier<byte[]>();

    public IValueNotifier<RtcConnectionState> State => _state;
    private MockRtcConnector Owner { get; init; }
    public WebRtcOffer Offer { get; init; }
    public WebRtcAnswer Answer { get; init; }

    public void Dispose()
    {
        var otherConnections = _existingConnections.Where(con => con.Offer == Offer && con.Answer == Answer && con.Owner != Owner).ToArray();
        foreach (var otherConnection in otherConnections)
        {
            otherConnection._state.Change(RtcConnectionState.Closed);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public Task<MediaState> GetMediaStateAsync() { throw new NotImplementedException(); }

    public Task<string> GetStateAsync() { throw new NotImplementedException(); }

    public Task SetLocalVideoTargetAsync(ElementReference videoElement) { throw new NotImplementedException(); }

    public Task SetMediaStateAsync(MediaState mediaState) { throw new NotImplementedException(); }

    public Task SetVideoTargetAsync(ElementReference videoElement) { throw new NotImplementedException(); }

    public ValueTask<bool> WriteBytesAsync(byte[] bytes) { throw new NotImplementedException(); }
}
