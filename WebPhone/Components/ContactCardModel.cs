using Microsoft.AspNetCore.Components;
using WebPhone.Services;

namespace WebPhone.Components;

public sealed record ContactCardModel(
    Contact Contact,
    string? Nickname,
    string ConnectionStatusText,
    string ConnectionStatusCssClass,
    bool IsConnecting,
    bool ChatReady,
    bool IsChatOpen,
    bool IsCallActive,
    IReadOnlyList<ChatMessage> ChatMessages,
    Action? ToggleFavorite,
    Action<string?>? SetNickname,
    Action? Notify,
    Action? Connect,
    Action? CancelConnect,
    Action? Disconnect,
    Action? StartCall,
    Action? AcceptCall,
    Action? EndCall,
    Action? CancelCall,
    Action<string>? SendMessage,
    Action<ElementReference>? OnRemoteAudioElementReady
);
