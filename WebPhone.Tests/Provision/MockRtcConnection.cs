using EverModern.Blazor.DirectCommunication;
using EverModern.Events;
using Microsoft.AspNetCore.Components;
using WebPhone.Domain;

namespace WebPhone.Tests.Provision;

class MockRtcConnection : IRtcConnection
{
    static readonly List<MockRtcConnection> _existingConnections = [];
    static readonly EventSource<(MockRtcConnection, byte[])> _bytesSent = new();

    readonly ObservedValue<RtcConnectionState> _state = new(RtcConnectionState.New);

    readonly Subscription _bytesExchangeSubscription;
    readonly EventSource<byte[]> _bytesReceived = new();

    public MockRtcConnection(MockRtcConnector owner, WebRtcOffer offer, WebRtcAnswer answer)
    {
        Owner = owner;
        Offer = offer;
        Answer = answer;

        Id = RtcMatchParameters.ComputeNegotiationId(Offer, Answer).ToString();
        _state.Change(RtcConnectionState.Connected);
        _existingConnections.Add(this);
        _bytesExchangeSubscription = _bytesSent.Subscribe(senderBytes =>
        {
            var (sender, bytes) = senderBytes;
            if (sender.Offer == Offer && sender.Answer == Answer && sender.Owner != Owner)
                _bytesReceived.Invoke(bytes);
        });

        Bytes = new(
            async bytes =>
            {
                _bytesSent.Invoke((this, bytes));
                return true;
            },
            _bytesReceived
        );
    }

    public string Id { get; }

    public IValueNotifier<RtcConnectionState> State => _state;

    private MockRtcConnector Owner { get; init; }
    public WebRtcOffer Offer { get; init; }
    public WebRtcAnswer Answer { get; init; }

    public BytesChannel Bytes { get; }

    public void Close()
    {
        Dispose();
        _state.Change(RtcConnectionState.Closed);
    }

    public void Dispose()
    {
        _bytesExchangeSubscription.Dispose();
        var otherConnections = _existingConnections
            .Where(con => con.Offer == Offer && con.Answer == Answer && con.Owner != Owner)
            .ToArray();
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

    public Task<MediaState> GetMediaStateAsync() =>
        Task.FromResult(new MediaState(new(false, false), new(false, false)));

    public Task SetLocalVideoTargetAsync(ElementReference videoElement) => Task.CompletedTask;

    public Task SetMediaStateAsync(MediaState mediaState) => Task.CompletedTask;

    public Task SetVideoTargetAsync(ElementReference videoElement) => Task.CompletedTask;

    public ValueTask<bool> WriteBytesAsync(byte[] bytes)
    {
        _bytesSent.Invoke((this, bytes));
        return ValueTask.FromResult(true);
    }
}
