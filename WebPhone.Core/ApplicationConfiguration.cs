using EverModern.Blazor.DirectCommunication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using WebPhone.Background;
using WebPhone.Channels;
using WebPhone.Data;

namespace WebPhone;

public static class ApplicationConfiguration
{
    public static void ConfigureWebPhoneFrontendApplication(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddSingleton(sp =>
            {
                var options = sp.GetRequiredService<PhoneOptions>();
                return new JsRtcConnector(
                    sp.GetRequiredService<IJSRuntime>(),
                    options.WebRtcIceServers
                );
            }
        );

        services.AddSingleton<IRtcConnector>(sp => sp.GetRequiredService<JsRtcConnector>());

        services.AddSingleton<PeerConnectionsDispatcher>();
        services.AddSingleton<IncomingConnectionsHandler>();
        services.AddSingleton<ContactsDispatcher>();
        services.AddSingleton<ContactsRepository>();
        services.AddSingleton<PresenceAnnouncer>();
        services.AddSingleton<AppStarter>();
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
#if DEBUG
                baseUrl = "http://localhost:5194";
#endif
                var externalChannelBaseUrl = new BackendClient(
                    baseUrl,
                    profile
                );
                return externalChannelBaseUrl;
            }
        );

        services.AddSingleton<IBackendClient>(sp => sp.GetRequiredService<BackendClient>());

        services.AddSingleton(new BackendMessagesChannel());

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
