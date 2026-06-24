using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using WebPhone;
using WebPhone.UI;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

IServiceCollection services = builder.Services;

services.ConfigureWebPhoneFrontendApplication(builder.Configuration);

builder.Logging.SetMinimumLevel(LogLevel.Debug);

var host = builder.Build();

await host.RunAsync();
