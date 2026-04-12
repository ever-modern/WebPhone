using System.Net.Http.Json;
using System.Text.Json;
using WebPhone.Contract;
using WebPhone.Services.Data;

namespace WebPhone.Services;

public class BackendClient(string baseUrl, IProfile profile) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    readonly HttpClient _httpClient = new();    
    readonly string _exchangeEndpoint = $"{baseUrl.TrimEnd('/')}/api/exchange";
    readonly string _pushSubscriptionEndpoint = $"{baseUrl.TrimEnd('/')}/api/subscribe-for-push";
    readonly string _notifyEndpoint = $"{baseUrl.TrimEnd('/')}/api/notify";

    public string ClientId => profile.User.Id;

    public async Task<ExchangeResponse> ExchangeAsync(MessageRequest[] outgoingMessages, DateTimeOffset cutoffDate, CancellationToken cancellationToken = default)
    {
        var requestStartTimestamp = DateTimeOffset.UtcNow;

        using var request = new HttpRequestMessage(HttpMethod.Post, _exchangeEndpoint)
        {
            Content = JsonContent.Create(new ExchangeRequest(ClientId, cutoffDate, outgoingMessages), options: JsonOptions)
        };
        request.Headers.Add("X-Client-Id", ClientId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var exchangeResponse = await response.Content.ReadFromJsonAsync<ExchangeResponse>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException();

        return exchangeResponse;
    }

    public async Task RegisterPushSubscriptionAsync(string subscriptionPayload, CancellationToken cancellationToken = default)
    {
        var body = subscriptionPayload;//JsonSerializer.Serialize(subscriptionPayload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, _pushSubscriptionEndpoint);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-Client-Id", ClientId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task NotifyAsync(string? targetClientId, string? message, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _notifyEndpoint)
        {
            Content = JsonContent.Create(new NotifyRequest(targetClientId, message), options: JsonOptions)
        };
        request.Headers.Add("X-Client-Id", ClientId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
        => _httpClient.Dispose();
}
