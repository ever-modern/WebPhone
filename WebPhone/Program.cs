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
builder.Services.AddScoped<IWebRtcConfigurator, PusherChannelsRegistrator>();
builder.Services.AddScoped<IWebRtcConnector, PusherChannelsRegistrator>();
builder.Services.AddScoped<IExternalChannel<Message>>(sp =>
{
    var options = sp.GetRequiredService<IOptions<PhoneOptions>>().Value;
    return new NetlifyMessagesChannel(options.ExternalChannelBaseUrl, options.ExternalChannelPollPath, options.PollIntervalMs);
});
builder.Services.Configure<PusherOptions>(builder.Configuration.GetSection("Pusher"));
builder.Services.Configure<PhoneOptions>(builder.Configuration.GetSection("Phone"));

await builder.Build().RunAsync();
