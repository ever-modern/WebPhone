using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using EverModern.Blazor.DirectCommunication;
using WebPhone;
using WebPhone.Registration;
using WebPhone.Registration.Pusher;
using WebPhone.Services;
using Microsoft.Extensions.Options;


var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

IServiceCollection services = builder.Services;

services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
services.AddSingleton<WebRtcInterop>();
services.AddScoped<Phone>();
services.AddScoped<ContactsRepository>();
services.AddScoped<PresenceAnnouncer>();
services.AddScoped<IncomingConnectionsHandler>();
services.AddScoped<IWebRtcConfigurator, AzureWebRtcChannelsRegistrator>();
services.AddScoped<IWebRtcRegistrator, AzureWebRtcChannelsRegistrator>();
services.AddSingleton<PhoneOptions>(sp => sp.GetRequiredService<IOptions<PhoneOptions>>().Value);

services.AddSingleton<RtcConnector>();

services.AddSingleton<BackendClient>(sp => 
{
    var clientId = sp.GetRequiredService<IProfile>().User.Id;
    var options = sp.GetRequiredService<PhoneOptions>();
    var baseUrl = options.ExternalChannelBaseUrl;
#if DEBUG
    baseUrl = "http://localhost:7272";
#endif
    var externalChannelBaseUrl = new BackendClient(baseUrl, clientId);
    return externalChannelBaseUrl;
});
services.AddSingleton<IMessagesChannel>(sp =>
{
    var options = sp.GetRequiredService<PhoneOptions>();
    return new AzureMessagesChannel(sp.GetRequiredService<BackendClient>(), options.PollIntervalMs);
});
services.Configure<PusherOptions>(builder.Configuration.GetSection("Pusher"));
services.Configure<PhoneOptions>(builder.Configuration.GetSection("Phone"));

services.AddSingleton<ILocalStore, BrowserLocalStore>();
services.AddSingleton<NicknamesRepository>();
services.AddSingleton<ProfileStore>();
services.AddSingleton<IProfile>(sp => sp.GetRequiredService<ProfileStore>());

var host = builder.Build();

await host.RunAsync();
