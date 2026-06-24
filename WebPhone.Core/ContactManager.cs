namespace WebPhone;

public class ContactManager(
    MediaConnection mediaConnection,
    string contactId,
    PeerConnectionsDispatcher peerConnectionsDispatcher
)
{
    public InteractionState Interaction => mediaConnection.State.Value;

    public Action? Disconnect =>
        Interaction is InteractionState.Connected
            ? () => _ = peerConnectionsDispatcher.ClosePeerConnectionAsync(contactId)
            : null;

    public Action? AudioCall =>
        Interaction
            is InteractionState.Connected
                and not InteractionState.Calling
                and not InteractionState.ReceivingCall
            ? () => _ = mediaConnection.Call()
            : null;

    public Action? VideoCall =>
        Interaction
            is InteractionState.Connected
                and not InteractionState.Calling
                and not InteractionState.ReceivingCall
            ? () => _ = mediaConnection.Call(true, true)
            : null;

    public Action? Connect =>
        Interaction is InteractionState.Disconnected
            ? () => _ = peerConnectionsDispatcher.ConnectAsync(contactId)
            : null;

    public Action? StopCalling =>
        Interaction is InteractionState.Calling ? () => _ = mediaConnection.StopCalling() : null;

    public Action? DeclineCall =>
        Interaction is InteractionState.ReceivingCall
            ? () => _ = mediaConnection.RejectCall()
            : null;
    public Action? Hangup =>
        Interaction is InteractionState.OnCall ? () => _ = mediaConnection.StopCall() : null;

    public Func<IReadOnlyList<string>>? GetChat =>
        Interaction is InteractionState.Offline ? null : () => [];

    public void SendMessage(string text)
    {
        return;
    }
}
