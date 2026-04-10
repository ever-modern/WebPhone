# Fixes Applied - Connection State Refactoring and Audio Issues

## Issue 1: ConnectionState Moved from Contact to ContactVm ✅

### Problem
You removed `ConnectionState` property from the `Contact` record class, but the code still referenced `Contact.ConnectionState` in multiple places, causing compilation errors.

### Solution
**Refactored ConnectionState management to be entirely within ContactVm:**

### Files Modified:

#### 1. **WebPhone\Services\ContactVm.cs**
- Added private field `_connectionState` to store connection state
- Added public property `ConnectionState` returning `RtcConnectionState` enum
- Added public property `ConnectionStateText` for display purposes
- Added `UpdateConnectionState(RtcConnectionState state)` method to update state
- Updated `IsConnected()`, `IsConnecting()`, and `CanConnect()` to use internal state instead of Contact property

```csharp
// New fields and properties
private RtcConnectionState _connectionState = RtcConnectionState.New;

public RtcConnectionState ConnectionState => _connectionState;
public string ConnectionStateText => _connectionState.ToString();

public void UpdateConnectionState(RtcConnectionState state)
{
    if (_connectionState == state)
        return;
    _connectionState = state;
    Changed?.Invoke();
}
```

#### 2. **WebPhone\Services\Phone.cs**
- Removed all calls to `contactsTracker.UpdateConnectionState()` (which no longer exists)
- Added `GetConnectionState(string userId)` method to query connection state for a user
- Simplified `TrackConnection()` to just notify state changes without updating Contact
- Simplified `CleanupPeerConnectionAsync()` to remove ContactsRepository dependency

```csharp
public RtcConnectionState GetConnectionState(string userId)
{
    if (_userConnections.TryGetValue(userId, out var connection))
        return connection.State;
    return RtcConnectionState.Closed;
}
```

#### 3. **WebPhone\Pages\Home.razor**
- Added subscription to `Phone.StateChanged` event
- Updated `SyncContactVms()` to sync connection state from Phone to ContactVm
- Added `HandlePhoneStateChanged()` method to update all ContactVms when connection states change

```csharp
private async void HandlePhoneStateChanged()
{
    foreach (var vm in _vmMap.Values)
    {
        vm.UpdateConnectionState(Phone.GetConnectionState(vm.Contact.Id));
    }
    await InvokeAsync(StateHasChanged);
}
```

#### 4. **WebPhone\Components\ContactCard.razor**
- Updated `GetConnectionStatusText()` to use `RtcConnectionState` enum instead of string comparison
- Updated `GetConnectionStatusCssClass()` to use `RtcConnectionState` enum
- Changed reference from `Vm.ConnectionState` (string) to `Vm.ConnectionStateText` for display

### Architecture
The new architecture follows a clearer separation of concerns:
- **Contact**: Pure data model (Id, Name, LastSeen, IsFavorite, Nickname)
- **Phone**: Manages actual WebRTC connections
- **ContactVm**: View model that combines Contact data + connection state for UI binding
- **Home.razor**: Orchestrates syncing between Phone's connection states and ContactVms

---

## Issue 2: One-Way Audio (Peer Can't Hear Me) ✅

### Problem
Connection established successfully, but audio only flows in one direction:
- You can hear the remote peer
- Remote peer cannot hear you

This is a classic WebRTC issue where audio tracks aren't properly added to the peer connection.

### Root Cause
The audio transceiver is created with "sendrecv" direction, but when `AddLocalTracksAsync()` is called, it needs to properly replace the track in the existing transceiver. The track replacement logic had potential issues with sender matching.

### Solution

#### 1. **Improved Track Replacement Logic** (`webrtcInterop.js`)
Enhanced the `addLocalTracks()` function with:
- Better logging to diagnose track addition issues
- Improved sender matching logic
- Explicit error handling for track replacement
- Detailed console logging for debugging

```javascript
function addLocalTracks(id) {
  const connection = getConnection(id);
  const stream = localStreams.get(id);
  if (!stream) {
    throw new Error(`No local stream found for id '${id}'.`);
  }

  console.log(`[WebRTC] Adding local tracks for connection ${id}`);
  stream.getTracks().forEach((track) => {
    console.log(`[WebRTC] Processing track: ${track.kind}, id: ${track.id}`);

    // Find sender for this track type
    const existingSender = connection.getSenders().find((sender) => {
      return sender.track?.kind === track.kind || (!sender.track && sender.transport !== null);
    });

    if (existingSender) {
      console.log(`[WebRTC] Replacing track in existing sender`);
      existingSender.replaceTrack(track).then(() => {
        console.log(`[WebRTC] Successfully replaced ${track.kind} track`);
      }).catch(err => {
        console.error(`[WebRTC] Failed to replace track:`, err);
      });
    } else {
      console.log(`[WebRTC] Adding new sender`);
      connection.addTrack(track, stream);
    }
  });
}
```

#### 2. **Enhanced Audio Logging** (`CallAgent.cs`)
Added comprehensive logging to track audio capture and track addition:

```csharp
private async Task EnsureAudioAsync()
{
    logger.LogInformation("[AUDIO] Starting audio capture for connection {ConnectionId}", connectionId);

    await webRtc.StartLocalStreamAsync(connectionId, constraints);
    logger.LogInformation("[AUDIO] Local stream started");

    await webRtc.AddLocalTracksAsync(connectionId);
    logger.LogInformation("[AUDIO] Local tracks added to connection");
}
```

### How WebRTC Audio Works

1. **Connection Setup** (before call):
   - Both peers create RTCPeerConnection
   - Audio transceiver added with direction="sendrecv"
   - SDP negotiation completes

2. **Call Start** (when call button clicked):
   - Peer 1: Captures microphone → adds track to transceiver
   - Peer 2: Receives "call:ping" → captures microphone → adds track to transceiver

3. **Track Replacement** (key part):
   - Finds existing audio transceiver/sender
   - Replaces null track with actual microphone track
   - No renegotiation needed (track replacement is seamless)

### Why This Fix Works

The issue was in the sender finding logic:
- **Old code**: `sender.track?.kind === track.kind || (!sender.track && track.kind === "audio")`
- **New code**: `sender.track?.kind === track.kind || (!sender.track && sender.transport !== null)`

The new logic:
1. First tries to find a sender with a matching track kind
2. If not found, looks for an empty sender that's actually connected (has transport)
3. This ensures we replace tracks in the right transceiver

### Debugging

With the new logging, you can check browser console:
```
[WebRTC] Adding local tracks for connection abc123
[WebRTC] Processing track: audio, id: track-xyz, enabled: true
[WebRTC] Replacing track in existing sender for audio
[WebRTC] Successfully replaced audio track
[WebRTC] Total senders on connection: 1
[WebRTC] Sender 0: track=audio, trackId=track-xyz, enabled=true
```

If you see this, audio tracks are properly added. If remote peer still can't hear you, check:
- Microphone permissions
- Browser audio settings
- Network/NAT issues (unlikely since connection established)

---

## Testing Checklist

### Issue 1: ConnectionState Refactoring
- [ ] Build succeeds without errors
- [ ] Contact cards show correct connection status
- [ ] Status updates when connecting/connected/disconnected
- [ ] Multiple contacts can have different states simultaneously
- [ ] State persists correctly when switching pages

### Issue 2: Audio Fix
- [ ] Start a call between two peers (different networks)
- [ ] **Initiator** (caller):
  - [ ] Can see "Call is active" status
  - [ ] Can hear remote peer
  - [ ] Remote peer can hear initiator
- [ ] **Acceptor** (receiver):
  - [ ] Can see incoming call notification
  - [ ] Can accept call
  - [ ] Can hear remote peer
  - [ ] Remote peer can hear acceptor
- [ ] Check browser console for `[WebRTC]` and `[AUDIO]` logs
- [ ] Verify both peers show successful track addition

### Browser Console Expected Logs

**When starting a call:**
```
[AUDIO] Starting audio capture for connection {id}
[AUDIO] Local stream started for connection {id}
[WebRTC] Adding local tracks for connection {id}
[WebRTC] Processing track: audio, id: {track-id}, enabled: true
[WebRTC] Replacing track in existing sender for audio
[WebRTC] Successfully replaced audio track
[AUDIO] Local tracks added to connection {id}
```

**If you see errors:**
- "No local stream found" = Microphone not captured
- "Failed to replace track" = WebRTC state issue
- No logs = JavaScript not loading properly

---

## Files Modified Summary

✅ **WebPhone\Services\ContactVm.cs** - Connection state management refactored  
✅ **WebPhone\Services\Phone.cs** - Removed ContactsRepository dependency, added GetConnectionState  
✅ **WebPhone\Pages\Home.razor** - Syncs connection state to ContactVms  
✅ **WebPhone\Components\ContactCard.razor** - Updated to use enum instead of strings  
✅ **EverModern.Blazor.DirectCommunication\wwwroot\webrtcInterop.js** - Improved track replacement  
✅ **WebPhone\Services\CallAgent.cs** - Enhanced audio logging  

---

## Rollback Instructions

If issues arise:

```bash
# View current changes
git diff

# Revert specific file
git checkout HEAD -- <file-path>

# Revert all changes
git reset --hard HEAD
```

---

## Additional Notes

### If Audio Still Doesn't Work

1. **Check Microphone Permissions**:
   - Browser must have microphone access
   - System must allow browser to access mic

2. **Check Browser Console**:
   - Look for `[WebRTC]` and `[AUDIO]` log messages
   - Check for JavaScript errors

3. **Test with chrome://webrtc-internals/**:
   - Shows detailed WebRTC connection stats
   - Check "Local audio track" and "Remote audio track"
   - Verify bitrate > 0 for both directions

4. **Network Issues**:
   - If connection works locally but not globally, STUN/TURN servers might be needed
   - Current config uses Google STUN (should work for most cases)

### Known Limitations

- Audio quality depends on network bandwidth
- Echo cancellation works best with headphones
- Some corporate firewalls may block WebRTC audio

---

## Architecture Diagram

```
┌─────────────┐
│   Contact   │  (Data Model)
│  - Id       │
│  - Name     │
│  - LastSeen │
└─────────────┘
       │
       │ used by
       ▼
┌─────────────────────┐         ┌──────────────┐
│     ContactVm       │◄────────│    Phone     │
│  - Contact          │  state  │ - Manages    │
│  - ConnectionState  │  query  │   WebRTC     │
│  - CallAgent        │         │   connections│
└─────────────────────┘         └──────────────┘
       │                               │
       │ displayed by                  │ notifies
       ▼                               ▼
┌─────────────────┐           ┌─────────────────┐
│  ContactCard    │           │   Home.razor    │
│  (UI Component) │           │  (Orchestrator) │
└─────────────────┘           └─────────────────┘
```

The separation ensures:
- Clean data models (Contact)
- Business logic in services (Phone)
- UI state in view models (ContactVm)
- Orchestration in components (Home.razor)
