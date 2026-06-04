using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WebPhone.Backend.Services;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

IServiceCollection services = builder.Services;

ServicesConfiguration.ConfigureWebPhoneBackendServices(services);

builder.Build().Run();
