using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using EverModern.Blazor.DirectCommunication;
using WebPhone;
using WebPhone.Registration;
using WebPhone.Registration.Pusher;
using Microsoft.Extensions.Configuration;
using WebPhone.Services;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<WebRtcInterop>();
builder.Services.AddScoped<Phone>();
builder.Services.AddScoped<IWebRtcConfigurator, AzureWebRtcChannelsRegistrator>();
builder.Services.AddScoped<IWebRtcConnector, AzureWebRtcChannelsRegistrator>();
builder.Services.AddSingleton<PhoneOptions>(sp => sp.GetRequiredService<IOptions<PhoneOptions>>().Value);
builder.Services.AddSingleton<BackendClient>(sp => 
{
    var options = sp.GetRequiredService<PhoneOptions>();
    var baseUrl = options.ExternalChannelBaseUrl;
#if DEBUG
    baseUrl = "http://localhost:7272";
#endif
    var externalChannelBaseUrl = new BackendClient(baseUrl);
    return externalChannelBaseUrl;
});
builder.Services.AddScoped<IMessagesChannel>(sp =>
{
    var options = sp.GetRequiredService<PhoneOptions>();
    return new AzureMessagesChannel(sp.GetRequiredService<BackendClient>(), options.PollIntervalMs);
});
builder.Services.Configure<PusherOptions>(builder.Configuration.GetSection("Pusher"));
builder.Services.Configure<PhoneOptions>(builder.Configuration.GetSection("Phone"));

var host = builder.Build();

var js = host.Services.GetRequiredService<IJSRuntime>();

await host.RunAsync();
