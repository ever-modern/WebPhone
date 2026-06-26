using EverModern.Blazor.DirectCommunication;
using EverModern.Threading.Locks;
using WebPhone.Domain;

namespace WebPhone;

public class RtcConnectionProcesses
{
    readonly Lock _locker = new();
    readonly List<RtcConnectionProcess> _connectionProcesses = [];

    public RtcConnectionProcess[] Processes
    {
        get
        {
            using var _ = _locker.LockScope();
            _connectionProcesses.RemoveAll(p => p is { IsUnderway: false, IsConnected: false });
            return [.._connectionProcesses];
        }
    }

    public Task<IRtcConnection?> WhenAny() => Task.Run(async () =>
        {
            var processes = _connectionProcesses.ToArray();
            if (processes.Length == 0)
                return null;
            await Task.WhenAny(processes.Select(p => (Task<IRtcConnection>)p).ToArray());
            var ready = Processes.FirstOrDefault(p => p.IsConnected);
            try
            {
                return await ready;
            }
            catch (Exception)
            {
                return null;
            }
        }
    );

    public IRtcConnection? AnyReady() => Processes.FirstOrDefault(p => p.IsConnected)?.Result;

    public void Add(Task<IRtcConnection> task, CancellationTokenSource cancellation, WebRtcOffer? offer)
    {
        using var _ = _locker.LockScope();
        if (_connectionProcesses.Any(p => p.Offer is not null && p.Offer == offer))
            return;

        _connectionProcesses.Add(new(task, cancellation, offer));
    }

    public void CloseAll()
    {
        using var _ = _locker.LockScope();

        _connectionProcesses.RemoveAll(p =>
            {
                p.Stop();
                if (p.IsConnected)
                    p.Result.Dispose();

                return true;
            }
        );
    }

    public InteractionState State
    {
        get
        {
            using var _ = _locker.LockScope();
            var isConnected = AnyReady();
            if (isConnected is not null)
                return InteractionState.Connected.Instance;

            if (Processes.Any(p => p.IsUnderway))
                return InteractionState.Connecting.Instance;

            return InteractionState.Disconnected.Instance;
        }
    }
}
