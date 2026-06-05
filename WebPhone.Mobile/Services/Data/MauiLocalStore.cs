using System.Text.Json;
using Microsoft.Maui.Storage;
using WebPhone.Services.Data;

namespace WebPhone.Android.Services.Data;

public class MauiLocalStore : ILocalStore
{
    public Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (!Preferences.ContainsKey(key))
            return Task.FromResult(default(T)!);

        var json = Preferences.Get(key, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
            return Task.FromResult(default(T)!);

        var value = JsonSerializer.Deserialize<T>(json);
        return Task.FromResult(value!);
    }

    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value);
        Preferences.Set(key, json);
        return Task.CompletedTask;
    }
}