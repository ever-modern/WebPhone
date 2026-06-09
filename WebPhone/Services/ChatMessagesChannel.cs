using System.Threading.Channels;
using WebPhone.Contract;
using WebPhone.Services.Data;

namespace WebPhone.Services;

/// <summary>
/// Scoped background service that provides persistent, backend-persisted chat.
/// 
/// Usage:
/// <code>
///   var reader = chatChannel.Subscribe(peerId);           // start polling for peerId
///   var sent   = await chatChannel.SendAsync(peerId, …);  // send + get echo immediately
///   await foreach (var msg in reader.ReadAllAsync(ct)) …  // receive new messages
/// </code>
/// </summary>
public sealed class ChatMessagesChannel(IBackendClient client) : IAsyncDisposable
{
    // Per-peer state ──────────────────────────────────────────────────────────
    private sealed class PeerState
    {
        public readonly Channel<ChatMessageDto> Channel =
            System.Threading.Channels.Channel.CreateUnbounded<ChatMessageDto>(
                new UnboundedChannelOptions { SingleWriter = true });
        public long Watermark;   // highest message ID delivered to the channel
    }

    private readonly Dictionary<string, PeerState> _peers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private Task? _pollLoop;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Subscribe to new messages from <paramref name="peerId"/>.
    /// Starts the background poll loop on first call.
    /// The caller should consume the reader with <c>await foreach</c>
    /// (pass a CancellationToken to stop when the component is disposed).
    /// </summary>
    public ChannelReader<ChatMessageDto> Subscribe(string peerId)
    {
        EnsurePeer(peerId);
        return _peers[peerId].Channel.Reader;
    }

    /// <summary>
    /// Load the conversation history (up to 50 most-recent messages).
    /// Call once on component mount for the initial list; Subscribe() handles live updates.
    /// </summary>
    public async Task<IReadOnlyList<ChatMessageDto>> GetHistoryAsync(
        string peerId,
        CancellationToken cancellationToken = default)
    {
        EnsurePeer(peerId);
        var messages = await client.GetChatMessagesAsync(peerId, sinceId: 0, cancellationToken);

        // Initialise watermark so the poll loop won't re-deliver anything already shown.
        if (messages.Length > 0)
            _peers[peerId].Watermark = messages[^1].Id;

        return messages;
    }

    /// <summary>
    /// Persist a message and return the <see cref="ChatMessageDto"/> with the server-assigned ID
    /// immediately (optimistic echo). The poll loop watermark is advanced so the message is
    /// not duplicated when the backend poll returns it.
    /// </summary>
    public async Task<ChatMessageDto> SendAsync(
        string peerId,
        string text,
        CancellationToken cancellationToken = default)
    {
        EnsurePeer(peerId);
        var dto = await client.SendChatMessageAsync(peerId, text, cancellationToken);

        // Advance watermark — the poll loop will skip this message.
        var state = _peers[peerId];
        state.Watermark = Math.Max(state.Watermark, dto.Id);

        return dto;
    }

    // ── Internal ───────────────────────────────────────────────────────────────

    private void EnsurePeer(string peerId)
    {
        if (_peers.ContainsKey(peerId)) return;
        _peers[peerId] = new PeerState();
        _pollLoop ??= RunPollLoopAsync(_cts.Token);
    }

    private async Task RunPollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { break; }

            foreach (var (peerId, state) in _peers)
            {
                try
                {
                    var messages = await client.GetChatMessagesAsync(
                        peerId, state.Watermark, ct);

                    foreach (var msg in messages)
                    {
                        if (msg.Id <= state.Watermark) continue; // already delivered
                        state.Watermark = msg.Id;
                        await state.Channel.Writer.WriteAsync(msg, ct);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch
                {
                    // Transient network error — skip this tick, retry next interval.
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        foreach (var state in _peers.Values)
            state.Channel.Writer.TryComplete();
    }
}
