# WebRTC Connection Debug Log Reference

## How to Debug WebRTC Connection Issues

### 1. Enable Debug Logging
In your `appsettings.Development.json`, ensure debug logging is enabled:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "WebPhone.Services.RtcConnector": "Debug",
      "WebPhone.Services.IncomingConnectionHandler": "Debug",
      "WebPhone.Services.Phone": "Debug"
    }
  }
}
```

### 2. Expected Log Flow for Successful Connection

#### Initiator Side (the peer clicking "Connect"):
```
[PHONE] ConnectToUserAsync called for userId: {peer}
[INITIATOR] InitiateConnectionAsync called for peer: {peer}
[INITIATOR] CreateRawInitiatedConnectionAsync - Generated connectionId: {id}
[INITIATOR] State set to Connecting for connectionId: {id}
[INITIATOR] Initializing WebRTC for connectionId: {id}
[INITIATOR] Creating data channel for connectionId: {id}
[INITIATOR] Creating offer for connectionId: {id}
[INITIATOR] Sending ConnectionAttempt message to peer: {peer}
[INITIATOR] Waiting for answer from peer: {peer}
[INITIATOR] Received answer from peer: {peer}
[INITIATOR] Setting remote description for connectionId: {id}
[INITIATOR] Remote description set. WebRTC negotiation complete. Waiting for actual connection...
[PHONE] Connection initiated for userId: {peer}, state: Connecting
[PHONE] Tracking new connection for userId: {peer}, state: Connecting
[WebRTC EVENT] ConnectionStateChanged - connectionId: {id}, new state: connecting
[WebRTC EVENT] ConnectionStateChanged - connectionId: {id}, new state: connected
[WebRTC EVENT] Connection ESTABLISHED for peer: {peer}
[PHONE] Connection state changed for userId: {peer}, new state: Connected
```

#### Acceptor Side (the peer receiving the connection):
```
[INCOMING] Received connection attempt from {peer}, connectionId: {id}
[ACCEPTOR] AcceptConnectionAsync called for peer: {peer}, connectionId: {id}
[ACCEPTOR] CreateRawAcceptedConnectionAsync - connectionId: {id}
[ACCEPTOR] State set to Connecting for connectionId: {id}
[ACCEPTOR] Initializing WebRTC for connectionId: {id}
[ACCEPTOR] Setting remote description (offer) for connectionId: {id}
[ACCEPTOR] Creating answer for connectionId: {id}
[ACCEPTOR] Sending answer to peer: {peer}
[ACCEPTOR] Answer sent. Staying in Connecting state until WebRTC connection establishes.
[INCOMING] Connection accepted from {peer}, state: Connecting
[PHONE] OnConnectionEstablished - userId: {peer}, state: Connecting
[PHONE] Tracking new connection for userId: {peer}, state: Connecting
[WebRTC EVENT] ConnectionStateChanged - connectionId: {id}, new state: connecting
[WebRTC EVENT] ConnectionStateChanged - connectionId: {id}, new state: connected
[WebRTC EVENT] Connection ESTABLISHED for peer: {peer}
[PHONE] Connection state changed for userId: {peer}, new state: Connected
```

### 3. Common Issues and Their Log Signatures

#### Issue: Connection hangs in "Connecting" state
**What to look for:**
- Missing `[WebRTC EVENT] ConnectionStateChanged - new state: connected`
- Possible causes:
  - ICE candidate exchange failing (firewall/NAT issues)
  - STUN/TURN server configuration problems
  - Network connectivity issues

**Log pattern:**
```
[INITIATOR/ACCEPTOR] State set to Connecting
... (no further WebRTC EVENT logs)
```

#### Issue: Connection shows "Connected" then immediately "Disconnected"
**What to look for:**
```
[WebRTC EVENT] Connection ESTABLISHED
[WebRTC EVENT] ConnectionStateChanged - new state: disconnected
```
This indicates the connection was established but then dropped, possibly due to:
- Network instability
- Peer closing the connection
- ICE connection timeout

#### Issue: Connection fails immediately
**What to look for:**
```
[WebRTC EVENT] ConnectionStateChanged - new state: failed
[WebRTC EVENT] Connection FAILED
```
This indicates WebRTC couldn't establish the connection at all.

#### Issue: One peer shows "Connected", the other doesn't (THE BUG WE FIXED)
**Before the fix:**
```
ACCEPTOR: [ACCEPTOR] Answer sent. (then immediately sets state to Connected) ❌
INITIATOR: (stays in Connecting, waiting for WebRTC to connect)
```

**After the fix:**
```
ACCEPTOR: [ACCEPTOR] Answer sent. Staying in Connecting state until WebRTC connection establishes. ✅
INITIATOR: [INITIATOR] Remote description set. Waiting for actual connection... ✅
... both wait for WebRTC EVENT
BOTH: [WebRTC EVENT] Connection ESTABLISHED ✅
```

### 4. Log Prefixes Quick Reference

| Prefix | Component | Purpose |
|--------|-----------|---------|
| `[PHONE]` | Phone service | High-level connection management |
| `[INITIATOR]` | RtcConnector | Peer initiating the connection |
| `[ACCEPTOR]` | RtcConnector | Peer accepting the connection |
| `[INCOMING]` | IncomingConnectionHandler | Processing incoming connection requests |
| `[WebRTC EVENT]` | RtcConnector | WebRTC native events (the source of truth for connection state) |

### 5. Debug Checklist

When connection fails:
1. ✅ Check both peers' logs (initiator and acceptor)
2. ✅ Verify signaling phase completes (offer/answer exchange)
3. ✅ Check for WebRTC connection state events
4. ✅ Verify ICE server configuration
5. ✅ Check browser console for WebRTC errors
6. ✅ Ensure Azure Functions are working (signaling server)
7. ✅ Test network connectivity between peers

### 6. Browser Developer Tools

In addition to server logs, check browser console for WebRTC details:
```javascript
// Enable verbose WebRTC logging in Chrome
chrome://webrtc-internals/
```

This shows:
- ICE candidate exchanges
- DTLS/SRTP negotiation
- Media/data channel states
- Detailed error messages
