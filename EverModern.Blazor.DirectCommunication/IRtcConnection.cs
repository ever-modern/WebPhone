using EverModern.Events;
using Microsoft.AspNetCore.Components;

namespace EverModern.Blazor.DirectCommunication;

public enum RtcConnectionState
{
    New,
    Connecting,
    Connected,
    Disconnected,
    Failed,
    Closed
}

public interface IRtcConnection
{
    INotifier<byte[]> BytesReceived { get; }
    IValueNotifier<RtcConnectionState> State { get; }

    void Dispose();
    ValueTask DisposeAsync();
    Task<MediaState> GetMediaStateAsync();

    Task SetLocalVideoTargetAsync(ElementReference videoElement);
    Task SetMediaStateAsync(MediaState mediaState);
    Task SetVideoTargetAsync(ElementReference videoElement);
    Task<bool> WriteBytesAsync(byte[] bytes);

    public static RtcConnectionState StateFromString(string stateString)
        => stateString.ToLowerInvariant() switch
        {
            "new" => RtcConnectionState.New,
            "connecting" => RtcConnectionState.Connecting,
            "connected" => RtcConnectionState.Connected,
            "disconnected" => RtcConnectionState.Disconnected,
            "failed" => RtcConnectionState.Failed,
            "closed" => RtcConnectionState.Closed,

            _ => throw new ArgumentOutOfRangeException(
                nameof(stateString),
                stateString,
                $"Unknown RTC connection state '{stateString}'."
            )
        };
}
