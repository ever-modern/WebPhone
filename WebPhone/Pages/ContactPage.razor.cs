using Microsoft.AspNetCore.Components;
using WebPhone.Components;

namespace WebPhone.Pages;

public partial class ContactPage
{
    [Parameter]
    public string ContactId { get; set; } = "";

    ContactCardModel? _card;
    string _lastContactId = "";
    CancellationTokenSource _retryCts = new();

    protected override void OnInitialized()
    {
        BoundToLifetime(Dispatcher.StateChanged.Subscribe(SyncAndRender));
    }

    protected override void OnParametersSet()
    {
        if (ContactId == _lastContactId)
            return;
        _lastContactId = ContactId;

        // Cancel the previous contact's retry loop before starting a fresh one.
        _retryCts.Cancel();
        _retryCts.Dispose();
        _retryCts = new CancellationTokenSource();

        SyncCard();
        _ = RunAutoConnectAsync(_retryCts.Token);
    }

    // Keeps trying to connect (and reconnect after drop) until the page is left.
    // Connect() is null when already connected/connecting — no double-connect risk.
    async Task RunAutoConnectAsync(CancellationToken ct)
    {
        const int maxFailedAttempts = 1;
        int failedAttempts = 0;
        while (!ct.IsCancellationRequested && failedAttempts < maxFailedAttempts)
        {
            var success = await TryConnect("poll");
            if (success is false)
            {
                failedAttempts++;
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    // Call this whenever we know the contact is disconnected so recovery is immediate.
    async Task<bool> TryConnect(string source)
    {
        var action = _card?.Actions.Connect;
        if (action is null)
            return false;

        Console.WriteLine(
            $"[CONNECT] ContactPage.TryConnect({source}): calling Connect for {ContactId}"
        );
        var success = await action.Invoke();

        return success;
    }

    void SyncCard()
    {
        var state = Dispatcher.State.Contacts.FirstOrDefault(c => c.Contact.Id == ContactId);
        _card = state is null
            ? null
            : new ContactCardModel(
                Contact: state.Contact,
                InteractionState: state.InteractionState,
                Actions: state.AvailableActions,
                OnRemoteAudioElementReady: null
            );
    }

    async void SyncAndRender()
    {
        SyncCard();
        await InvokeAsync(StateHasChanged);
    }

    public override void Dispose()
    {
        _retryCts.Cancel();
        _retryCts.Dispose();
        base.Dispose();
    }
}
