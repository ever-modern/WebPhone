using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using WebPhone.AzureEnd.Services;
using WebPhone.AzureEnd.Storage;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services.AddScoped(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetValue<string>("WebPhoneDbConnectionString")
        ?? throw new InvalidOperationException("Postgres connection string is not configured.");

    // Do not open a DB connection in DI construction path.
    // Opening here can block function-start coordination and cause HTTP trigger timeout
    // before the invocation actually begins.
    return new NpgsqlConnection(connectionString);
});

builder.Services.AddScoped<MessagesRepository>();
builder.Services.AddScoped<ProfileSettingsRepository>();
builder.Services.AddScoped<ContactSettingsRepository>();
builder.Services.AddScoped<PushSubscriptionsRepository>();
builder.Services.AddScoped<PushNotificationService>();

builder.Build().Run();
