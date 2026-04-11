using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using EverModern.Blazor.DirectCommunication;
using WebPhone;
using WebPhone.Services;
using Microsoft.Extensions.Options;


var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

IServiceCollection services = builder.Services;

services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
services.AddScoped<WebRtcConnector>();
services.AddScoped<PeerConnector>();
services.AddScoped<WebRtcConnectionCoordinator>();
services.AddScoped<IncomingConnectionsHandler>();
services.AddScoped<ContactsDispatcher>();
services.AddScoped<ContactsRepository>();
services.AddScoped<PresenceAnnouncer>();
services.AddScoped<IWebRtcConfigurator, AzureWebRtcChannelsRegistrator>();
services.AddScoped<IWebRtcRegistrator, AzureWebRtcChannelsRegistrator>();
services.AddSingleton<PhoneOptions>(sp => sp.GetRequiredService<IOptions<PhoneOptions>>().Value);

services.AddSingleton<BackendClient>(sp => 
{
    var profile = sp.GetRequiredService<IProfile>();
    var options = sp.GetRequiredService<PhoneOptions>();
    var baseUrl = options.ExternalChannelBaseUrl;
#if DEBUG
    baseUrl = "http://localhost:7272";
#endif
    var externalChannelBaseUrl = new BackendClient(baseUrl, profile);
    return externalChannelBaseUrl;
});
services.AddSingleton<AzureMessagesChannel>(sp =>
{
    var options = sp.GetRequiredService<PhoneOptions>();
    return new AzureMessagesChannel(sp.GetRequiredService<BackendClient>(), options.PollIntervalMs);
});

services.AddSingleton<IMessagesChannel>(sp => sp.GetRequiredService<AzureMessagesChannel>());

services.Configure<PhoneOptions>(builder.Configuration.GetSection("Phone"));

services.AddSingleton<ILocalStore, BrowserLocalStore>();
services.AddSingleton<NicknamesRepository>();
services.AddSingleton<ProfileStore>();
services.AddSingleton<IProfile>(sp => sp.GetRequiredService<ProfileStore>());

builder.Logging.SetMinimumLevel(LogLevel.Debug);

var host = builder.Build();

await host.RunAsync();
