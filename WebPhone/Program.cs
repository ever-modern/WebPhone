using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using EverModern.Blazor.DirectCommunication;
using WebPhone;
using WebPhone.Registration;
using WebPhone.Registration.Pusher;
using Microsoft.Extensions.Configuration;
using WebPhone.Services;
using Microsoft.Extensions.Options;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<WebRtcService>();
builder.Services.AddScoped<PhoneService>();
#if DEBUG && False
builder.Services.AddScoped<IWebRtcConfigurator, MockWebRtcChannelsRegistrator>();
builder.Services.AddScoped<IWebRtcConnector, MockWebRtcChannelsRegistrator>();
builder.Services.AddScoped<IExternalChannel<Message>>(_ => new MockNetlifyMessagesChannel());
#else
builder.Services.AddScoped<IWebRtcConfigurator, AzureWebRtcChannelsRegistrator>();
builder.Services.AddScoped<IWebRtcConnector, AzureWebRtcChannelsRegistrator>();
builder.Services.AddScoped<IExternalChannel<Message>>(sp =>
{
    var options = sp.GetRequiredService<IOptions<PhoneOptions>>().Value;
    var baseAddress = builder.HostEnvironment.BaseAddress;
    var publishUrl = new Uri(new Uri(baseAddress), options.ExternalChannelPublishUrl).ToString();
    var readUrl = new Uri(new Uri(baseAddress), options.ExternalChannelReadUrl).ToString();
    return new AzureMessagesChannel(publishUrl, readUrl, options.PollIntervalMs);
});
#endif
builder.Services.Configure<PusherOptions>(builder.Configuration.GetSection("Pusher"));
builder.Services.Configure<PhoneOptions>(builder.Configuration.GetSection("Phone"));

await builder.Build().RunAsync();
