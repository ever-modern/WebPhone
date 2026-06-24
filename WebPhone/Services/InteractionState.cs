using EverModern.Blazor.DirectCommunication;
using WebPhone.Domain;

namespace WebPhone.Services;

public abstract class InteractionState
{
    public class Offline : InteractionState
    {
        public static Offline Instance { get; } = new Offline();
    }

    public class Disconnected : InteractionState
    {
        public static Disconnected Instance { get; } = new Disconnected();
    }

    public class Connecting : InteractionState
    {
        public static Connecting Instance { get; } = new Connecting();
    }

    public class Connected : InteractionState
    {
        public static Connected Instance { get; } = new Connected();
    }

    public class CallRequested : Connected
    {
        public long Id { get; init; } = CommonIdsGenerator.NewId();
        public bool Video { get; init; }
        public bool Audio { get; init; }
    }

    public class ReceivingCall : CallRequested;

    public class Calling : CallRequested;

    public class OnCall : Connected
    {
        public required MediaState MediaState { get; init; }
    }
}
