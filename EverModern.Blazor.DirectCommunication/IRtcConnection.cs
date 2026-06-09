using EverModern.Events;
using Microsoft.AspNetCore.Components;

namespace EverModern.Blazor.DirectCommunication
{
    public interface IRtcConnection
    {
        INotifier<byte[]> BytesReceived { get; }
        INotifier<string> StateChanged { get; }

        void Dispose();
        ValueTask DisposeAsync();
        Task<MediaState> GetMediaStateAsync();
        Task<string> GetStateAsync();
        Task SetLocalVideoTargetAsync(ElementReference videoElement);
        Task SetMediaStateAsync(MediaState mediaState);
        Task SetVideoTargetAsync(ElementReference videoElement);
        Task<bool> WriteBytesAsync(byte[] bytes);
    }
}