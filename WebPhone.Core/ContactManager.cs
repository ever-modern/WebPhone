using WebPhone.Data;

namespace WebPhone;

public class ContactManager(
    Contact contact,
    MediaConnection mediaConnection,
    PeerConnectionsDispatcher peerConnectionsDispatcher,
    ContactsRepository contactsRepository
)
{
    public Contact Contact => contact;

    public InteractionState Interaction => mediaConnection.State.Value is InteractionState.Disconnected ? TellInteractivity(Contact.LastSeen) : mediaConnection.State.Value;

    public Action? Disconnect =>
        Interaction is InteractionState.Connected ? () => _ = peerConnectionsDispatcher.DisconnectFromPeerAsync(contact.Id) : null;

    public Action? AudioCall =>
        Interaction
            is InteractionState.Connected and not InteractionState.Calling and not InteractionState.ReceivingCall ?
            () => _ = mediaConnection.Call() :
            null;

    public Action? VideoCall =>
        Interaction
            is InteractionState.Connected and not InteractionState.Calling and not InteractionState.ReceivingCall ?
            () => _ = mediaConnection.Call(true, true) :
            null;

    public Action? Connect =>
        Interaction is InteractionState.Disconnected ? () => _ = peerConnectionsDispatcher.ConnectAsync(contact.Id) : null;

    public Action? StopCalling =>
        Interaction is InteractionState.Calling ? () => _ = mediaConnection.StopCalling() : null;

    public Action? DeclineCall =>
        Interaction is InteractionState.ReceivingCall ? () => _ = mediaConnection.RejectCall() : null;

    public Action? AcceptCall =>
        Interaction is InteractionState.ReceivingCall ? () => _ = mediaConnection.AcceptCall() : null;

    public Action? Hangup =>
        Interaction is InteractionState.OnCall ? () => _ = mediaConnection.StopCall() : null;

    public Action? ToggleFavorite =>
        () => _ = contactsRepository.ToggleFavoriteAsync(contact.Id);

    public Action<string?> SetNickname =>
        (nickname) => _ = contactsRepository.SetNicknameAsync(contact.Id, nickname);

    public Func<IReadOnlyList<string>>? GetChat =>
        Interaction is InteractionState.Offline ? null : () => [];

    public void SendMessage(string text) { return; }

    static InteractionState TellInteractivity(DateTimeOffset lastSeen) => DateTimeOffset.UtcNow - lastSeen > TimeSpan.FromSeconds(10) ? InteractionState.Offline.Instance : InteractionState.Disconnected.Instance;
}
