using Microsoft.AspNetCore.Components;
using WebPhone.Background;

namespace WebPhone.UI.Pages;

public partial class ContactPage
{
    [Inject]
    public AppStarter AppStarter { get; set; } = null!;

    [Parameter]
    public string ContactId { get; set; } = "";

    ContactManager? Manager =>
        Dispatcher.State.Value.Contacts.FirstOrDefault(c => c.Contact.Id == ContactId);

    string _lastContactId = "";
    CancellationTokenSource _retryCts = new();

    protected override void OnInitialized()
    {
        BoundToLifetime(Dispatcher.State.Subscribe(_ => InvokeAsync(StateHasChanged)));
    }

    protected override async Task OnInitializedAsync()
    {
        await AppStarter.EnsureStartedAsync();
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

        _ = RunAutoConnectAsync(_retryCts.Token);
    }

    // Keeps trying to connect (and reconnect after drop) until the page is left.
    async Task RunAutoConnectAsync(CancellationToken ct)
    {
        await Task.Delay(5000);
        while (!ct.IsCancellationRequested)
        {
            var mgr = Manager;
            if (mgr?.Interaction is not InteractionState.Connected and not InteractionState.Offline)
            {
                Connect("poll");
            }

            return;
            await Task.Delay(TimeSpan.FromSeconds(3), ct);

        }
    }

    void Connect(string source)
    {
        var action = Manager?.Connect;
        if (action is null)
            return;

        Console.WriteLine(
            $"[CONNECT] ContactPage.TryConnect({source}): calling Connect for {ContactId}"
        );
        action.Invoke();
    }

    public override void Dispose()
    {
        _retryCts.Cancel();
        _retryCts.Dispose();
        base.Dispose();
    }
}
