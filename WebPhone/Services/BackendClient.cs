using System.Net.Http.Json;
using System.Text.Json;
using WebPhone.Contract;
using WebPhone.Services.Data;
namespace WebPhone.Services;

public class BackendClient(string baseUrl, IProfile profile) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    readonly HttpClient _httpClient = new();
    readonly string _exchangeEndpoint = $"{baseUrl.TrimEnd('/')}/api/exchange";
    readonly string _pushSubscriptionEndpoint = $"{baseUrl.TrimEnd('/')}/api/subscribe-for-push";
    readonly string _notifyEndpoint = $"{baseUrl.TrimEnd('/')}/api/notify";
    readonly string _chatSendEndpoint = $"{baseUrl.TrimEnd('/')}/api/chat/send";
    readonly string _chatMessagesEndpoint = $"{baseUrl.TrimEnd('/')}/api/chat/messages";
    readonly string _profileSettingsEndpoint = $"{baseUrl.TrimEnd('/')}/api/profiles";
    readonly string _contactSettingsEndpoint = $"{baseUrl.TrimEnd('/')}/api/contacts";

    public async Task<ExchangeResponse> ExchangeAsync(
        MessageRequest[] outgoingMessages,
        long messagesSinceId,
        CancellationToken cancellationToken = default
    )
    {
        string сlientId = profile.User.Id;

        using var request = new HttpRequestMessage(HttpMethod.Post, _exchangeEndpoint)
        {
            Content = JsonContent.Create(
                new ExchangeRequest(сlientId, messagesSinceId, outgoingMessages),
                options: JsonOptions
            ),
        };
        request.Headers.Add("X-Client-Id", сlientId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var exchangeResponse =
            await response.Content.ReadFromJsonAsync<ExchangeResponse>(
                JsonOptions,
                cancellationToken
            ) ?? throw new InvalidOperationException();

        return exchangeResponse;
    }

    public async Task RegisterPushSubscriptionAsync(
        string subscriptionPayload,
        CancellationToken cancellationToken = default
    )
    {
        string сlientId = profile.User.Id;
        var body = subscriptionPayload; //JsonSerializer.Serialize(subscriptionPayload, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, _pushSubscriptionEndpoint);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add("X-Client-Id", сlientId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task NotifyAsync(
        string? targetClientId,
        string? message,
        CancellationToken cancellationToken = default
    )
    {
        string сlientId = profile.User.Id;
        using var request = new HttpRequestMessage(HttpMethod.Post, _notifyEndpoint)
        {
            Content = JsonContent.Create(
                new NotifyRequest(targetClientId, message),
                options: JsonOptions
            ),
        };
        request.Headers.Add("X-Client-Id", сlientId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Sends a persistent chat message to <paramref name="recipientId"/> via the Azure backend.
    /// Returns the persisted <see cref="ChatMessageDto"/> with the server-assigned ID.
    /// </summary>
    public async Task<ChatMessageDto> SendChatMessageAsync(
        string recipientId,
        string text,
        CancellationToken cancellationToken = default)
    {
        string clientId = profile.User.Id;
        using var request = new HttpRequestMessage(HttpMethod.Post, _chatSendEndpoint)
        {
            Content = JsonContent.Create(new ChatSendRequest(text, recipientId), options: JsonOptions),
        };
        request.Headers.Add("X-Client-Id", clientId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ChatMessageDto>(JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Empty response from chat/send.");
    }

    /// <summary>
    /// Fetches chat messages between the current user and <paramref name="peerId"/>.
    /// When <paramref name="sinceId"/> is 0 (default) the server returns the most recent batch.
    /// Pass the last-seen ID to get only new messages (incremental polling).
    /// </summary>
    public async Task<ChatMessageDto[]> GetChatMessagesAsync(
        string peerId,
        long sinceId = 0,
        CancellationToken cancellationToken = default)
    {
        string clientId = profile.User.Id;
        var url = $"{_chatMessagesEndpoint}?peerId={Uri.EscapeDataString(peerId)}&sinceId={sinceId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Client-Id", clientId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ChatMessageDto[]>(JsonOptions, cancellationToken)
            ?? [];
    }

    public async Task<UserSettingsDto> GetUserSettingsAsync(CancellationToken cancellationToken = default)
    {
        string clientId = profile.User.Id;
        using var request = new HttpRequestMessage(HttpMethod.Get, _profileSettingsEndpoint);
        request.Headers.Add("X-Client-Id", clientId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<UserSettingsDto>(JsonOptions, cancellationToken)
            ?? new UserSettingsDto("", true, true, false);
    }

    public async Task UpsertUserSettingsAsync(UserSettingsDto dto, CancellationToken cancellationToken = default)
    {
        string clientId = profile.User.Id;
        using var request = new HttpRequestMessage(HttpMethod.Post, _profileSettingsEndpoint)
        {
            Content = JsonContent.Create(dto, options: JsonOptions)
        };
        request.Headers.Add("X-Client-Id", clientId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ContactSettingsDto[]> GetAllContactSettingsAsync(CancellationToken cancellationToken = default)
    {
        string clientId = profile.User.Id;
        using var request = new HttpRequestMessage(HttpMethod.Get, _contactSettingsEndpoint);
        request.Headers.Add("X-Client-Id", clientId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ContactSettingsDto[]>(JsonOptions, cancellationToken)
            ?? [];
    }

    public async Task<ContactSettingsDto> GetContactSettingsAsync(string contactId, CancellationToken cancellationToken = default)
    {
        string clientId = profile.User.Id;
        var url = $"{_contactSettingsEndpoint}?contactId={Uri.EscapeDataString(contactId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Client-Id", clientId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ContactSettingsDto>(JsonOptions, cancellationToken)
            ?? new ContactSettingsDto(clientId, contactId, false, true, true, null);
    }

    public async Task UpsertContactSettingsAsync(ContactSettingsDto dto, CancellationToken cancellationToken = default)
    {
        string clientId = profile.User.Id;
        using var request = new HttpRequestMessage(HttpMethod.Post, _contactSettingsEndpoint)
        {
            Content = JsonContent.Create(dto with { OwnerId = clientId }, options: JsonOptions)
        };
        request.Headers.Add("X-Client-Id", clientId);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _httpClient.Dispose();
}
