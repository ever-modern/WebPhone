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
builder.Services.AddScoped<WebRtcService>();
builder.Services.AddScoped<PhoneService>();
builder.Services.AddScoped<IWebRtcConfigurator, PusherChannelsRegistrator>();
builder.Services.AddScoped<IWebRtcConnector, PusherChannelsRegistrator>();
builder.Services.AddScoped<IExternalChannel<Message>>(sp =>
{
    var options = sp.GetRequiredService<IOptions<PhoneOptions>>().Value;
    var baseAddress = builder.HostEnvironment.BaseAddress;
    var absoluteBaseUrl = new Uri(new Uri(baseAddress), options.ExternalChannelBaseUrl).ToString();
    var pusherOptions = sp.GetRequiredService<IOptions<PusherOptions>>().Value;
    var channelName = $"private-{pusherOptions.ChannelPrefix}-lobby";
    return new NetlifyMessagesChannel(sp.GetRequiredService<IJSRuntime>(), absoluteBaseUrl, channelName, options.PollIntervalMs);
});
builder.Services.Configure<PusherOptions>(builder.Configuration.GetSection("Pusher"));
builder.Services.Configure<PhoneOptions>(builder.Configuration.GetSection("Phone"));

await builder.Build().RunAsync();
