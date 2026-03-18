using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using WebPhone.Registration;

namespace WebPhone.Registration.Pusher;

public sealed class PusherChannelsRegistrator(
    IJSRuntime jsRuntime,
    HttpClient httpClient,
    IOptions<PusherOptions> options) : IWebRtcConfigurator, IWebRtcConnector
{
    private readonly IJSRuntime jsRuntime = jsRuntime;
    private readonly HttpClient httpClient = httpClient;
    private readonly PusherOptions options = options.Value;
    private string? currentChannel;
    private string? currentEvent;
    private string? secretOverride;

    public ValueTask ConfigureAsync(ChannelsConfiguration configuration, CancellationToken cancellationToken = default)
    {
        secretOverride = configuration.Secret;
        return ValueTask.CompletedTask;
    }

    public async ValueTask InitializeAsync(string channelName, string eventName, CancellationToken cancellationToken = default)
    {
        if (string.Equals(currentChannel, channelName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(currentEvent, eventName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await DisposeSubscriptionAsync();

        await jsRuntime.InvokeAsync<bool>(
            "pusherInterop.subscribe",
            cancellationToken,
            options.Key,
            options.Cluster,
            channelName,
            eventName,
            options.EnableLogging,
            options.AuthUrl,
            GetSecret());

        currentChannel = channelName;
        currentEvent = eventName;
    }

    public async ValueTask PublishAsync(string channelName, string eventName, object payload, CancellationToken cancellationToken = default)
    {
        if (options.UseClientEvents)
        {
            var published = await jsRuntime.InvokeAsync<bool>("pusherInterop.publish", cancellationToken, channelName, eventName, payload);
            if (published)
            {
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(options.ProxyUrl))
        {
            throw new InvalidOperationException("Client events are unavailable and no proxy URL is configured.");
        }

        var secret = GetSecret();

        var fallbackEvent = eventName.StartsWith("client-", StringComparison.OrdinalIgnoreCase)
            ? eventName["client-".Length..]
            : eventName;

        var proxyPayload = new
        {
            appId = options.AppId,
            key = options.Key,
            secret,
            cluster = options.Cluster,
            channel = channelName,
            eventName = fallbackEvent,
            data = payload
        };

        var proxyJson = JsonSerializer.Serialize(proxyPayload);
        using var proxyRequest = new HttpRequestMessage(HttpMethod.Post, options.ProxyUrl)
        {
            Content = new StringContent(proxyJson, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json"))
        };

        using var proxyResponse = await httpClient.SendAsync(proxyRequest, cancellationToken);
        proxyResponse.EnsureSuccessStatusCode();
    }

    private string? GetSecret()
        => string.IsNullOrWhiteSpace(secretOverride) ? options.Secret : secretOverride;

    public async ValueTask<IReadOnlyList<Message>> PollMessagesAsync(string channelName, CancellationToken cancellationToken = default)
    {
        var messages = await jsRuntime.InvokeAsync<Message[]>("pusherInterop.poll", cancellationToken, channelName);
        return messages;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeSubscriptionAsync();
    }

    private async ValueTask DisposeSubscriptionAsync()
    {
        if (currentChannel is null || currentEvent is null)
        {
            return;
        }

        await jsRuntime.InvokeVoidAsync("pusherInterop.unsubscribe", currentChannel);
        currentChannel = null;
        currentEvent = null;
    }

}
