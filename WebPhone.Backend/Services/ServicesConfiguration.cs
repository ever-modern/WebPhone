using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebPhone.AzureEnd.Services;
using WebPhone.AzureEnd.Storage;

namespace WebPhone.Backend.Services;

public static class ServicesConfiguration
{
    public static IServiceCollection ConfigureWebPhoneBackendServices(this IServiceCollection services)
    {
        services.AddScoped(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetRequiredSection("WebPhoneDbConnectionString").Value
                ?? throw new InvalidOperationException("Postgres connection string is not configured.");

            // Do not open a DB connection in DI construction path.
            // Opening here can block function-start coordination and cause HTTP trigger timeout
            // before the invocation actually begins.
            return new NpgsqlConnection(connectionString);
        });

        services.AddScoped<MessagesRepository>();
        services.AddScoped<ProfileSettingsRepository>();
        services.AddScoped<ContactSettingsRepository>();
        services.AddScoped<PushSubscriptionsRepository>();
        services.AddScoped<PushNotificationService>();

        return services;
    }
}
