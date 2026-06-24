using EverModern.Events;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using WebPhone.Components;
using WebPhone.Domain;
using WebPhone.Services;

namespace WebPhone.UI.Layout;

public partial class MainLayout
{
    bool ready;
    bool _initialized;
    bool _isMobile;
    bool _profileMenuOpen;
    string _registerName = "";
    DotNetObjectReference<MainLayout>? _mobileRef;
    IReadOnlyList<ContactCardModel> _contactCards = [];
    string? _selectedId;

    Subscription? _dispatcherSub;
    Subscription? _profileSub;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);

        if (!firstRender || _initialized)
            return;

        _initialized = true;

        await AppStarter.EnsureStartedAsync();

        _dispatcherSub = Dispatcher.State.Subscribe(OnDispatcherChanged);
        _profileSub = Profile.UserChanged.Subscribe(_ => InvokeAsync(StateHasChanged));
        Nav.LocationChanged += OnLocationChanged;

        _isMobile = await JS.InvokeAsync<bool>("appInterop.isMobile");
        _mobileRef = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("appInterop.startMobileWatcher", _mobileRef);

        SyncSelectedId();
        SyncCards();

        ready = true;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Called by the matchMedia watcher whenever the viewport crosses 767 px.</summary>
    [JSInvokable]
    public void SetMobile(bool isMobile)
    {
        if (_isMobile == isMobile)
            return;
        _isMobile = isMobile;
        InvokeAsync(StateHasChanged);
    }

    void OnLocationChanged(
        object? sender,
        Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e
    )
    {
        _profileMenuOpen = false;
        SyncSelectedId();
        InvokeAsync(StateHasChanged);
    }

    void OnDispatcherChanged()
    {
        SyncCards();
        InvokeAsync(StateHasChanged);
    }

    void SyncSelectedId()
    {
        var path = new Uri(Nav.Uri).AbsolutePath;
        const string prefix = "/contact/";
        _selectedId = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(path[prefix.Length..])
            : null;
    }

    void SyncCards()
    {
        _contactCards = Dispatcher
            .State.Contacts.Select(s => new ContactCardModel(
                Contact: s.Contact,
                InteractionState: s.InteractionState,
                Actions: s.AvailableActions,
                OnRemoteAudioElementReady: null
            ))
            .ToList();
    }

    void HandleContactSelected(string id) => Nav.NavigateTo($"/contact/{Uri.EscapeDataString(id)}");

    string GetAvatarText() =>
        string.IsNullOrWhiteSpace(Profile.User.Name)
            ? "👤"
            : Profile.User.Name[0].ToString().ToUpperInvariant();

    void ToggleProfileMenu() => _profileMenuOpen = !_profileMenuOpen;

    void CloseProfileMenu() => _profileMenuOpen = false;

    async Task RegisterAsync()
    {
        var name = _registerName.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return;

        profileStore.SetUser(profileStore.User with { Name = name });
        await BackendClient.UpsertUserSettingsAsync(
            new UserSettingsDto(name, true, true, false)
        );
        _registerName = name;
        await InvokeAsync(StateHasChanged);
    }

    async Task HandleRegisterKeyDownAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            await RegisterAsync();
    }

    public void Dispose()
    {
        _mobileRef?.Dispose();
        _dispatcherSub?.Dispose();
        _profileSub?.Dispose();
        Nav.LocationChanged -= OnLocationChanged;
        Dispatcher.Dispose();
    }
}
