using System.Net.Http.Json;
using System.Text.Json;
using WebPhone.Contract;

namespace WebPhone.Services;

public class BackendClient(string baseUrl, string clientId) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    readonly HttpClient _httpClient = new();    
    readonly string _exchangeEndpoint = $"{baseUrl.TrimEnd('/')}/api/exchange";
    readonly string _pushSubscriptionEndpoint = $"{baseUrl.TrimEnd('/')}/api/subscribe-for-push";

    public string ClientId => clientId;

    public async Task<ExchangeResponse> ExchangeAsync(MessageRequest[] outgoingMessages, DateTimeOffset cutoffDate, CancellationToken cancellationToken = default)
    {
        var requestStartTimestamp = DateTimeOffset.UtcNow;

        using var request = new HttpRequestMessage(HttpMethod.Post, _exchangeEndpoint)
        {
            Content = JsonContent.Create(new ExchangeRequest(clientId, cutoffDate, outgoingMessages), options: JsonOptions)
        };
        request.Headers.Add("X-Client-Id", clientId);

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
        request.Headers.Add("X-Client-Id", clientId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
        => _httpClient.Dispose();
}
