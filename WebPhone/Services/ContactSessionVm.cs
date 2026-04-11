using EverModern.Events;
using Microsoft.AspNetCore.Components;

namespace WebPhone.Services;

public sealed class ContactSessionVm : IAsyncDisposable
{
    private const string CallPingMessage = "call:ping";
    private const string CallAcceptedMessage = "call:accepted";
    private static readonly TimeSpan IncomingCallPingTimeout = TimeSpan.FromSeconds(3);

    private readonly WebRtcConnectionCoordinator _coordinator;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _connectCts;
    private Subscription? _bytesSubscription;
    private DateTimeOffset _lastCallPingAt;
    private readonly Task _callPingMonitorTask;
    private CancellationTokenSource? _callPingLoopCts;
    private Task? _callPingLoopTask;
    private bool _isConnecting;
    private RtcConnectionState _connectionState = RtcConnectionState.New;
    private readonly List<ChatMessage> _chatMessages = [];

    readonly EventSource _changed = new();
    public INotifier Changed => _changed;

    public ContactSessionVm(Contact contact, WebRtcConnectionCoordinator coordinator)
    {
        Contact = contact;
        _coordinator = coordinator;
        _callPingMonitorTask = RunCallPingMonitorAsync();
    }

    public Contact Contact { get; private set; }

    public bool ChatReady => _bytesSubscription is not null && IsConnected();

    public IReadOnlyList<ChatMessage> ChatMessages => _chatMessages;

    public bool IsCallActive { get; private set; }

    public bool HasIncomingCallOffer =>
        DateTimeOffset.UtcNow - _lastCallPingAt <= IncomingCallPingTimeout;

    public RtcConnectionState ConnectionState => _connectionState;

    public string ConnectionStateText => _connectionState.ToString();

    public void UpdateContact(Contact contact)
    {
        Contact = contact;
        _changed.Invoke();
    }

    public async Task ConnectAsync()
    {
        if (_isConnecting || IsConnected())
            return;

        _isConnecting = true;
        UpdateConnectionState(RtcConnectionState.Connecting);

        _connectCts?.Cancel();
        _connectCts?.Dispose();
        _connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        try
        {
            await _coordinator.ConnectToUserAsync(Contact.Id, _connectCts.Token);
            await EnsureBytesSubscriptionAsync(_connectCts.Token);
            UpdateConnectionState(_coordinator.GetConnectionState(Contact.Id));
        }
        catch (OperationCanceledException)
        {
            UpdateConnectionState(RtcConnectionState.Closed);
        }
        catch
        {
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
        await _coordinator.DisconnectAsync(Contact.Id);
        DisposeBytesSubscription();
        UpdateConnectionState(RtcConnectionState.Closed);
        _isConnecting = false;
    }

    public async Task DisconnectAsync()
    {
        await StopCallIfNeededAsync();
        await _coordinator.DisconnectAsync(Contact.Id);
        DisposeBytesSubscription();
        UpdateConnectionState(RtcConnectionState.Closed);
    }

    public async Task StartCallAsync()
    {
        if (IsCallActive)
            return;

        IsCallActive = true;
        StartCallPingLoop();
        await SendSystemMessageAsync(CallPingMessage);
        _changed.Invoke();
    }

    public async Task AcceptIncomingCallAsync()
    {
        IsCallActive = true;
        _lastCallPingAt = DateTimeOffset.MinValue;
        await SendSystemMessageAsync(CallAcceptedMessage);
        _changed.Invoke();
    }

    public async Task EndCallAsync()
    {
        await StopCallIfNeededAsync();
    }

    public async Task CancelCallAsync()
    {
        await StopCallIfNeededAsync();
    }

    public Task SetRemoteAudioElementAsync(ElementReference audioElement) => Task.CompletedTask;

    public Task NotifyPeerAsync() =>
        _coordinator.NotifyClientAsync(Contact.Id, $"Notification from {_coordinator.DisplayName}", _cts.Token);

    public async Task SendChatMessageAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !IsConnected())
            return;

        await _coordinator.SendBytesAsync(Contact.Id, ToWireMessage(new RtcTextMessage(text, false)), _cts.Token);
        _chatMessages.Add(new ChatMessage("self", text, true));
        _changed.Invoke();
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

    private async Task EnsureBytesSubscriptionAsync(CancellationToken cancellationToken)
    {
        if (_bytesSubscription is not null)
            return;

        _bytesSubscription = await _coordinator.SubscribeBytesAsync(Contact.Id, OnBytesReceived, cancellationToken);
        _changed.Invoke();
    }

    private void DisposeBytesSubscription()
    {
        _bytesSubscription?.Dispose();
        _bytesSubscription = null;
        _changed.Invoke();
    }

    private void OnBytesReceived(byte[] payload)
    {
        if (!TryParseWireMessage(payload, out var parsed))
            return;

        if (parsed.IsSystem)
        {
            _ = HandleSystemMessageAsync(parsed.Text);
            return;
        }

        _chatMessages.Add(new ChatMessage("peer", parsed.Text, false));
        _changed.Invoke();
    }

    private async Task HandleSystemMessageAsync(string text)
    {
        if (text == CallPingMessage)
        {
            _lastCallPingAt = DateTimeOffset.UtcNow;
            _changed.Invoke();
        }
        else if (text == CallAcceptedMessage)
        {
            IsCallActive = true;
            _changed.Invoke();
        }

        await Task.CompletedTask;
    }

    private async Task SendSystemMessageAsync(string text)
    {
        await _coordinator.SendBytesAsync(Contact.Id, ToWireMessage(new RtcTextMessage(text, true)), _cts.Token);
    }

    private void StartCallPingLoop()
    {
        _callPingLoopCts?.Cancel();
        _callPingLoopCts?.Dispose();
        _callPingLoopCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        _callPingLoopTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(900));
            while (await timer.WaitForNextTickAsync(_callPingLoopCts.Token))
            {
                await SendSystemMessageAsync(CallPingMessage);
            }
        }, _callPingLoopCts.Token);
    }

    private async Task StopCallIfNeededAsync()
    {
        _callPingLoopCts?.Cancel();
        _callPingLoopCts?.Dispose();
        _callPingLoopCts = null;

        if (_callPingLoopTask is not null)
        {
            try
            {
                await _callPingLoopTask;
            }
            catch
            {
            }

            _callPingLoopTask = null;
        }

        IsCallActive = false;
        _changed.Invoke();
    }

    private Task RunCallPingMonitorAsync() =>
        Task.Run(async () =>
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
        }, _cts.Token);

    public async ValueTask DisposeAsync()
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        DisposeBytesSubscription();
        await StopCallIfNeededAsync();

        _cts.Cancel();
        try
        {
            await _callPingMonitorTask;
        }
        catch
        {
        }

        _cts.Dispose();
    }

    private static byte[] ToWireMessage(RtcTextMessage message)
    {
        var textBytes = System.Text.Encoding.UTF8.GetBytes(message.Text);
        var result = new byte[textBytes.Length + 1];
        result[0] = message.IsSystem ? (byte)1 : (byte)0;
        textBytes.CopyTo(result, 1);
        return result;
    }

    private static bool TryParseWireMessage(byte[] rawMessage, out RtcTextMessage parsed)
    {
        if (rawMessage.Length == 0)
        {
            parsed = default!;
            return false;
        }

        var isSystem = rawMessage[0] == 1;
        var text = rawMessage.Length > 1
            ? System.Text.Encoding.UTF8.GetString(rawMessage, 1, rawMessage.Length - 1)
            : string.Empty;
        parsed = new RtcTextMessage(text, isSystem);
        return true;
    }
}
