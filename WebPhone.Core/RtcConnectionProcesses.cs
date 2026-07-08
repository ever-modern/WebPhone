using EverModern.Blazor.DirectCommunication;
using EverModern.Threading.Locks;
using WebPhone.Domain;

namespace WebPhone;

public class RtcConnectionProcesses
{
    readonly Lock _locker = new();
    readonly List<RtcConnectionProcess> _connectionProcesses = [];

    public RtcConnectionProcess[] RelevantProcesses
    {
        get
        {
            using var _ = _locker.LockScope();
            _connectionProcesses.RemoveAll(p => p is { IsUnderway: false, IsConnected: false });
            return [.._connectionProcesses.OrderBy(p => p.IsConnected).ThenBy(p => p.IsConnected ? p.Result.Id : "")];
        }
    }

    public Task<IRtcConnection?> WhenAny() => Task.Run(async () =>
        {
            while (true)
            {
                var ready = AnyReady();
                if (ready is not null)
                    return ready;

                RtcConnectionProcess[] snapshot;
                lock (_locker)
                {
                    snapshot = _connectionProcesses.ToArray();
                }

                if (snapshot.Length == 0)
                    return null;

                var pollDelay = Task.Delay(TimeSpan.FromMilliseconds(100));
                var allTasks = new Task[snapshot.Length + 1];
                allTasks[0] = pollDelay;
                for (int i = 0; i < snapshot.Length; i++)
                    allTasks[i + 1] = snapshot[i];

                var completed = await Task.WhenAny(allTasks);

                if (completed == pollDelay)
                    continue;

                var completedRtc = (Task<IRtcConnection>)completed;
                var result = completedRtc.IsCompletedSuccessfully ? completedRtc.Result : null;
                if (result is not null)
                    return result;
            }
        }
    );

    public IRtcConnection? AnyReady() => RelevantProcesses.FirstOrDefault(p => p.IsConnected)?.Result;

    public void Add(Task<IRtcConnection> task, CancellationTokenSource cancellation, WebRtcOffer? offer)
    {
        using var _ = _locker.LockScope();
        if (_connectionProcesses.Any(p => p.Offer is not null && p.Offer == offer))
            return;

        _connectionProcesses.Add(new(task, cancellation, offer));
    }

    public IRtcConnection[] DrainAll()
    {
        using var _ = _locker.LockScope();
        var result = _connectionProcesses.Select(p =>
                {
                    p.Stop();
                    return p.IsConnected ? p.Result : null;
                }
            )
            .Where(p => p is not null)
            .ToArray();
        _connectionProcesses.Clear();
        return result;
    }

    public InteractionState State
    {
        get
        {
            using var _ = _locker.LockScope();
            var isConnected = AnyReady();
            if (isConnected is not null)
                return InteractionState.Connected.Instance;

            if (RelevantProcesses.Any(p => p.IsUnderway))
                return InteractionState.Connecting.Instance;

            return InteractionState.Disconnected.Instance;
        }
    }
}
