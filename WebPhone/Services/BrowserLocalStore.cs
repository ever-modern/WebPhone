using System.Text.Json;
using Microsoft.JSInterop;

namespace WebPhone.Services;

public class BrowserLocalStore(IJSRuntime js) : ILocalStore
{
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var json = await js.InvokeAsync<string>("localStorage.getItem", key);
        var result = json is not null ? JsonSerializer.Deserialize<T>(json) : default;
        return result;
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        CancellationToken cancellationToken = default
    )
    {
        var json = JsonSerializer.Serialize(value);
        await js.InvokeVoidAsync("localStorage.setItem", key, json);
    }
}
