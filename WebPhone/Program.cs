using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using EverModern.Blazor.DirectCommunication;
using WebPhone;
using WebPhone.Registration;
using WebPhone.Registration.Pusher;
using WebPhone.Services;
using Microsoft.Extensions.Options;

string clientId = Guid.NewGuid().ToString("N");

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

IServiceCollection services = builder.Services;

services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
services.AddScoped<WebRtcInterop>();
services.AddScoped<Phon>();
services.AddScoped<IWebRtcConfigurator, AzureWebRtcChannelsRegistrator>();
services.AddScoped<IWebRtcConnector, AzureWebRtcChannelsRegistrator>();
services.AddSingleton<PhoneOptions>(sp => sp.GetRequiredService<IOptions<PhoneOptions>>().Value);
services.AddSingleton<BackendClient>(sp => 
{
    var options = sp.GetRequiredService<PhoneOptions>();
    var baseUrl = options.ExternalChannelBaseUrl;
#if DEBUG
    baseUrl = "http://localhost:7272";
#endif
    var externalChannelBaseUrl = new BackendClient(baseUrl, clientId);
    return externalChannelBaseUrl;
});
services.AddScoped<IMessagesChannel>(sp =>
{
    var options = sp.GetRequiredService<PhoneOptions>();
    return new AzureMessagesChannel(sp.GetRequiredService<BackendClient>(), options.PollIntervalMs);
});
services.Configure<PusherOptions>(builder.Configuration.GetSection("Pusher"));
services.Configure<PhoneOptions>(builder.Configuration.GetSection("Phone"));

services.AddSingleton<ILocalStore, BrowserLocalStore>();
services.AddSingleton<Profile>();

var host = builder.Build();

var js = host.Services.GetRequiredService<Profile>();

var user = await js.GetUserInfoAsync();

clientId = user?.Id ?? clientId;

await host.RunAsync();
