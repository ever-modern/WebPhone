using Microsoft.AspNetCore.Components;
using WebPhone.Messages;
using WebPhone.Services;

namespace WebPhone.Components;

public record InteractionState(
    bool IsConnected = false,
    string ConnectionState = "None",
    bool IsConnecting = false,
    bool ChatReady = false,
    bool IsCallActive = false,
    bool IsCalling = false,
    bool HasIncomingCall = false,
    IReadOnlyList<ChatMessage> Chat = null
);

public record ContactActions(
    Action? ToggleFavorite = null,
    Action<string?>? SetNickname = null,
    Action? Notify = null,
    Func<Task<bool>>? Connect = null,
    Action? CancelConnect = null,
    Action? Disconnect = null,
    Action? StartCall = null,
    Action? StartVideoCall = null,
    Action? AcceptCall = null,
    Action? DeclineCall = null,
    Action? EndCall = null,
    Action? CancelCall = null,
    Action<string>? SendMessage = null
);

public sealed record ContactCardModel(
    Contact Contact,
    InteractionState InteractionState,
    ContactActions Actions,
    Action<ElementReference>? OnRemoteAudioElementReady
);
