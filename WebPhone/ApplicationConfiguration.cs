using EverModern.Blazor.DirectCommunication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using WebPhone.Services;
using WebPhone.Services.Background;
using WebPhone.Services.Channels;
using WebPhone.Services.Data;

namespace WebPhone;

public static class ApplicationConfiguration
{
    public static void ConfigureWebPhoneApplication(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddScoped(sp =>
        {
            var options = sp.GetRequiredService<PhoneOptions>();
            return new RtcConnector(sp.GetRequiredService<IJSRuntime>(), options.WebRtcIceServers);
        });
        services.AddScoped<PeerConnector>();
        services.AddScoped<IncomingConnectionsHandler>();
        services.AddScoped<ContactsDispatcher>();
        services.AddScoped<VideoCallState>();
        services.AddScoped<ContactsRepository>();
        services.AddScoped<PresenceAnnouncer>();
        services.AddScoped<IWebRtcConfigurator, AzureWebRtcChannelsRegistrator>();
        services.AddScoped<IWebRtcRegistrator, AzureWebRtcChannelsRegistrator>();
        services.AddSingleton<PhoneOptions>(sp =>
            sp.GetRequiredService<IOptions<PhoneOptions>>().Value
        );

        services.AddSingleton<BackendClient>(sp =>
        {
            var profile = sp.GetRequiredService<IProfile>();
            var options = sp.GetRequiredService<PhoneOptions>();
            var baseUrl = options.ExternalChannelBaseUrl;
            baseUrl = "https://web-phone-api.enjoyer-station.myvnc.com";
            var externalChannelBaseUrl = new BackendClient(baseUrl, profile);
            return externalChannelBaseUrl;
        });

        services.AddSingleton<BackendMessagesChannel>(sp =>
        {
            var options = sp.GetRequiredService<PhoneOptions>();
            return new BackendMessagesChannel(
                sp.GetRequiredService<BackendClient>(),
                options.PollIntervalMs
            );
        });

        services.AddSingleton<IMessagesChannel>(sp =>
            sp.GetRequiredService<BackendMessagesChannel>()
        );

        services.AddSingleton<ChatMessagesChannel>();

        services.Configure<PhoneOptions>(configuration.GetSection("Phone"));

        services.AddSingleton<ILocalStore, BrowserLocalStore>();
        services.AddSingleton<ProfileStore>();
        services.AddSingleton<IProfile>(sp => sp.GetRequiredService<ProfileStore>());
    }
}
