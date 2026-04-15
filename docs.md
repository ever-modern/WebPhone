# WebPhone – Current Architecture Notes

## Solution structure
- `WebPhone`: Blazor WebAssembly client
- `WebPhone.Contract`: shared contracts/DTOs (`ExchangeRequest`, `MessageResponse`, `CommonIdsGenerator`)
- `WebPhone.AzureEnd`: Azure Functions backend with PostgreSQL persistence
- `EverModern.Blazor.DirectCommunication`: WebRTC interop (C# + TypeScript bundle)

## Client runtime overview

### `ContactsDispatcher`
- Builds `PhoneState` for UI (`Home.razor` -> `ContactCardModel`).
- Keeps per-contact runtime in a single dictionary of contexts.
- Delegates interaction/call state management to `ContactManager` for each contact.
- Keeps chat cache per contact for UI messages.
- Reacts to:
  - `ContactsRepository.StateChanged`
  - `PeerConnector.StateChanged`
  - each `ContactManager.StateChanged`

### `ContactManager`
- Per-contact state coordinator.
- Owns an internal interaction object (`InteractionType` + cancel action) with automatic cleanup on transitions.
- Handles transitions:
  - `None` -> `Connecting` -> `Connected`
  - `Connected` -> `Calling` / `ReceivingCall`
  - `Calling` / `ReceivingCall` -> `Speaking`
  - end/cancel -> `Connected` or `None`
- Uses transition guards to avoid async race-driven invalid state jumps.
- Manages call maintenance through `CallMaintainer`.
- Enables/disables media via `RtcConnection`.

### `PeerConnector`
- Owns active `RtcConnection`s by peer ID.
- Handles offer/answer exchange over `IMessagesChannel`.
- Resolves connection collision by comparing request IDs.

## Messaging/backend flow
- `AzureMessagesChannel` exchanges messages with backend using `MessagesSinceId` cutoff.
- Backend `ExchangeFunction` writes outgoing rows then returns relevant incoming rows.
- `MessagesRepository` uses `id > @SinceId` filtering.

## Call signaling flow (current)
1. Connect peers through `PeerConnector`.
2. Caller enters `Calling` and sends periodic `RtcMessageType.WantCall`.
3. Callee receives ping, moves to `ReceivingCall`.
4. On accept, callee starts maintenance and both sides move to `Speaking`.
5. Call timeout/reject/end transitions back to `Connected`.

## Hotspots
- `WebPhone/Services/ContactManager.cs`
- `WebPhone/Services/ContactsDispatcher.cs`
- `WebPhone/Services/CallMaintainer.cs`
- `EverModern.Blazor.DirectCommunication/ts/src/rtcConnectionManager.ts`

## Build checks
- `WebPhone/WebPhone.csproj`
- `WebPhone.AzureEnd/WebPhone.AzureEnd.csproj`
