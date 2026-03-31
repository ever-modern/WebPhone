using Microsoft.JSInterop;
using System.Text.Json;

namespace WebPhone.Services;

public class BrowserLocalStore(IJSRuntime js) : ILocalStore
{
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var json = await js.InvokeAsync<string?>("appInterop.getLocalStorageItem", key);
        var result = json is not null ? JsonSerializer.Deserialize<T>(json) : default;
        return result;
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value);
        await js.InvokeVoidAsync("appInterop.setLocalStorageItem", key, value);
    }
}
