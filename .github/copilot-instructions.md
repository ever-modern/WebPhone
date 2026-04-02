# Copilot Instructions

## Project Guidelines
- User prefers not to use CSS isolation and wants styles moved to a general CSS file.
- User prefers Phone to act only as a factory/coordinator (creating CallAgent and RtcMessageChannel) and not store chat messages; chat handling should go through RtcMessageChannel subscriptions in components/services that consume it.