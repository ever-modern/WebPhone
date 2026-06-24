namespace WebPhone;

/// <summary>Coordinates the fullscreen video call UI across all pages.</summary>
public sealed class VideoCallState
{
    public bool IsOpen { get; private set; }
    public string? ContactId { get; private set; }

    public event Action? Changed;

    public void Open(string contactId)
    {
        IsOpen = true;
        ContactId = contactId;
        Changed?.Invoke();
    }

    public void Close()
    {
        IsOpen = false;
        ContactId = null;
        Changed?.Invoke();
    }
}
