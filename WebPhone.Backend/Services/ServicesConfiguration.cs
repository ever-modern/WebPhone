using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using WebPhone.Backend.Actions;
using WebPhone.Backend.Storage;

namespace WebPhone.Backend.Services;

public static class ServicesConfiguration
{
    public static IServiceCollection ConfigureWebPhoneBackendServices(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.AddScoped(sp =>
        {
            var connectionString =
                configuration.GetRequiredSection("WebPhoneDbConnectionString").Value
                ?? throw new InvalidOperationException(
                    "Postgres connection string is not configured."
                );

            // Do not open a DB connection in DI construction path.
            // Opening here can block function-start coordination and cause HTTP trigger timeout
            // before the invocation actually begins.
            return new NpgsqlConnection(connectionString);
        });

        services.AddScoped<MessagesRepository>();
        services.AddScoped<ProfileSettingsRepository>();
        services.AddScoped<ContactSettingsRepository>();
        services.AddScoped<PushSubscriptionsRepository>();
        services.AddScoped<PushNotifier>();
        services.AddSingleton<RtcMatchMaker>();

        services.AddScoped<ExchangeApiAction>();
        services.AddScoped<NotifyApiAction>();
        services.AddScoped<SubscriptionApiAction>();
        services.AddScoped<GetProfileSettingsApiAction>();
        services.AddScoped<UpsertProfileSettingsApiAction>();
        services.AddScoped<GetContactSettingsApiAction>();
        services.AddScoped<UpsertContactSettingsApiAction>();
        services.AddScoped<SendChatApiAction>();
        services.AddScoped<GetChatMessagesApiAction>();
        services.AddScoped<HealthCheckApiAction>();
        services.AddScoped<RtcConnectAction>();

        services.AddHttpContextAccessor();

        services.AddExceptionMapper(
            configuration,
            builder =>
            {
                builder.Map<UserFaultException>().ToStatusCode(400);
            }
        );

        services.AddScoped<RequestSupplements>(sp =>
        {
            var headers =
                sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Headers;
            var clientId = headers?["X-Client-Id"].FirstOrDefault();

            return new(clientId);
        });

        return services;
    }
}
