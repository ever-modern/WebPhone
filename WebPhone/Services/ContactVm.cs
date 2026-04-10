using System.Runtime.CompilerServices;
using System.Threading.Channels;
using EverModern.Events;
using Microsoft.AspNetCore.Components;

namespace WebPhone.Services;

public sealed class ContactVm : IAsyncDisposable
{
    private const string CallPingMessage = "call:ping";
    private const string CallAcceptedMessage = "call:accepted";
    private static readonly TimeSpan IncomingCallPingTimeout = TimeSpan.FromSeconds(3);

    private readonly Phone _phone;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _connectCts;
    private RtcMessageChannel? _channel;
    private CallAgent? _callAgent;
    private DateTimeOffset _lastCallPingAt;
    private readonly Task _callPingMonitorTask;
    private bool _isConnecting;
    private RtcConnectionState _connectionState = RtcConnectionState.New;
    private IDisposable? _callAgentSub;

    readonly EventSource _changed = new();
    public INotifier Changed => _changed;

    public ContactVm(Contact contact, Phone phone)
    {
        Contact = contact;
        _phone = phone;
        _callPingMonitorTask = RunCallPingMonitorAsync();

        if (IsConnected())
            _ = EnsureChatSubscriptionAsync();
    }

    public Contact Contact { get; private set; }

    public bool ChatReady { get; private set; }

    public bool IsCallActive => _callAgent?.CallState is CallState.Active or CallState.Ringing;

    public bool HasIncomingCallOffer =>
        DateTimeOffset.UtcNow - _lastCallPingAt <= IncomingCallPingTimeout;

    public RtcConnectionState ConnectionState => _connectionState;

    public string ConnectionStateText => _connectionState.ToString();

    public void UpdateContact(Contact contact)
    {
        Contact = contact;

        if (IsConnected() && _channel is null && !_isConnecting)
            _ = EnsureChatSubscriptionAsync();
        else if (!IsConnected() && _channel is not null)
            ResetChatChannelState();

        _changed.Invoke();
    }

    public async Task ConnectAsync()
    {
        if (_isConnecting || IsConnected())
            return;

        _isConnecting = true;

        // Immediately update UI to show "Connecting" state
        UpdateConnectionState(RtcConnectionState.Connecting);

        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = new CancellationTokenSource();

        try
        {
            await _phone.ConnectToUserAsync(Contact.Id, _connectCts.Token);
            await EnsureChatSubscriptionAsync();
        }
        catch (OperationCanceledException)
        {
            // Reset to disconnected state when cancelled
            UpdateConnectionState(RtcConnectionState.Closed);
        }
        catch
        {
            // Reset to failed state on error
            UpdateConnectionState(RtcConnectionState.Failed);
            throw;
        }
        finally
        {
            _isConnecting = false;
        }
    }

    public async Task CancelConnectAsync()
    {
        _connectCts?.Cancel();
        await _phone.CancelConnectionAsync(Contact.Id);
        ResetChatChannelState();

        // Reset connection state to closed
        UpdateConnectionState(RtcConnectionState.Closed);
        _isConnecting = false;
    }

    public async Task DisconnectAsync()
    {
        await StopCallIfNeededAsync();
        await _phone.CancelConnectionAsync(Contact.Id);
        ResetChatChannelState();
    }

    public async Task ToggleCallAsync(ElementReference audioElement)
    {
        if (IsCallActive)
        {
            await StopCallIfNeededAsync();
            return;
        }

        _callAgent = await _phone.CreateCallAgentAsync(Contact.Id, _cts.Token);
        _callAgentSub?.Dispose();
        _callAgentSub = _callAgent.StateChanged.Subscribe(NotifyChanged);
        await _callAgent.StartOutgoingCallAsync(audioElement, _cts.Token);
    }

    public async Task AcceptIncomingCallAsync(ElementReference audioElement)
    {
        _callAgent ??= await _phone.CreateCallAgentAsync(Contact.Id, _cts.Token);
        _callAgentSub?.Dispose();
        _callAgentSub = _callAgent.StateChanged.Subscribe(NotifyChanged);
        await _callAgent.AcceptCallAsync(audioElement);
        _lastCallPingAt = DateTimeOffset.MinValue;
    }

    public Task NotifyPeerAsync() =>
        _phone.NotifyClientAsync(Contact.Id, $"Notification from {_phone.DisplayName}", _cts.Token);

    public async IAsyncEnumerable<(string Sender, string Text)> SubscribeToChatAsync(
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        if (_channel is null)
            yield break;

        using var subscription = _channel.Subscribe();
        await foreach (var message in subscription.ReadAllAsync(ct))
            yield return (message.IsSystem ? "system" : "peer", message.Text);
    }

    public async Task SendChatMessageAsync(string text, CancellationToken ct)
    {
        if (_channel is null || !IsConnected())
            return;
        try
        {
            await _channel.Writer.WriteAsync(new RtcTextMessage(text, false), ct);
        }
        catch (ChannelClosedException) { }
    }

    public async Task HandleSystemMessageAsync(string text, ElementReference audioElement)
    {
        if (text == CallPingMessage)
        {
            _lastCallPingAt = DateTimeOffset.UtcNow;
            _changed.Invoke();
        }
        else if (text == CallAcceptedMessage)
        {
            if (_callAgent is not null)
                await _callAgent.AttachRemoteAudioAsync(audioElement);
            _changed.Invoke();
        }
    }

    public void UpdateConnectionState(RtcConnectionState state)
    {
        if (_connectionState == state)
            return;
        _connectionState = state;
        _changed.Invoke();
    }

    public bool IsConnected() => _connectionState == RtcConnectionState.Connected;

    public bool IsConnecting() => _connectionState == RtcConnectionState.Connecting;

    public bool CanConnect() =>
        _connectionState
            is RtcConnectionState.Closed
                or RtcConnectionState.Disconnected
                or RtcConnectionState.Failed
                or RtcConnectionState.New;

    private void NotifyChanged() => _changed.Invoke();

    private async Task EnsureChatSubscriptionAsync()
    {
        _channel ??= await _phone.GetTextChannelAsync(Contact.Id, _cts.Token);
        ChatReady = true;
        _changed.Invoke();
    }

    private void ResetChatChannelState()
    {
        ChatReady = false;
        _channel = null;
    }

    private async Task StopCallIfNeededAsync()
    {
        if (_callAgent is null)
            return;

        _callAgentSub?.Dispose();
        _callAgentSub = null;
        await _callAgent.CancelCallAsync();
        _callAgent = null;
    }

    private Task RunCallPingMonitorAsync() =>
        Task.Run(
            async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
                var prevHadOffer = false;
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    var hasOffer = HasIncomingCallOffer;
                    if (prevHadOffer && !hasOffer)
                        _changed.Invoke();
                    prevHadOffer = hasOffer;
                }
            },
            _cts.Token
        );

    public async ValueTask DisposeAsync()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        await StopCallIfNeededAsync();
        _callAgentSub?.Dispose();
        _cts.Cancel();
        try
        {
            await _callPingMonitorTask;
        }
        catch { }
        _cts.Dispose();
    }
}
