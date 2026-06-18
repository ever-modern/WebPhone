using System.Runtime.CompilerServices;
using EverModern.Blazor.DirectCommunication;

namespace WebPhone.Services;

class ConnectionEstablishmentProcess(
    Task<IRtcConnection?> source,
    CancellationTokenSource cts
)
{
    public TaskAwaiter<IRtcConnection?> GetAwaiter() => source.GetAwaiter();

    public bool ConnectedSuccessfully =>
        source.IsCompletedSuccessfully && source.Result is not null;

    public bool IsCompleted => source.IsCompleted;

    public bool Cancel()
    {
        cts.Cancel();

        return IsCompleted == false;
    }

    public IRtcConnection? Result => source.Result;

    public Task<IRtcConnection?> WaitAsync(CancellationToken cancellationToken) =>
        source.WaitAsync(cancellationToken);
}
