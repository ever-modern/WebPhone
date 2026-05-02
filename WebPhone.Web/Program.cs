using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using WebPhone;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

IServiceCollection services = builder.Services;

services.ConfigureWebPhoneApplication(builder.Configuration);

builder.Logging.SetMinimumLevel(LogLevel.Debug);

var host = builder.Build();

await host.RunAsync();
