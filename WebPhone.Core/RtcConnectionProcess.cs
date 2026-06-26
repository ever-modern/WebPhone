using System.Runtime.CompilerServices;
using EverModern.Blazor.DirectCommunication;
using WebPhone.Domain;

namespace WebPhone;

public class RtcConnectionProcess(
    Task<IRtcConnection> task,
    CancellationTokenSource cancellation,
    WebRtcOffer? offer
)
{
    public void Stop() { cancellation.Cancel(); }

    public bool IsConnected => task is { IsCompletedSuccessfully: true, Result.State.Value: RtcConnectionState.Connected };

    public bool IsFaulted => task.IsFaulted;

    public bool IsUnderway => task.IsCompleted == false;

    readonly Task<IRtcConnection> _task = task;

    public static implicit operator Task(RtcConnectionProcess process) => process._task;
    public static implicit operator Task<IRtcConnection>(RtcConnectionProcess process) => process._task;

    public TaskAwaiter<IRtcConnection> GetAwaiter() => _task.GetAwaiter();

    public IRtcConnection Result => _task.Result;

    public WebRtcOffer? Offer => offer;
}
