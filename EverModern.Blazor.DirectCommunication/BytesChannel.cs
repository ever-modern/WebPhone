using EverModern.Events;

namespace EverModern.Blazor.DirectCommunication;

public record BytesChannel(Func<byte[], ValueTask<bool>> WriteAsync, INotifier<byte[]> Received)
    : SimpleChannel<byte[]>(WriteAsync, Received);

public record SimpleChannel<T>(Func<T, ValueTask<bool>> WriteAsync, INotifier<T> Received);
