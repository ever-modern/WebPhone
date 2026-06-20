using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton(sp =>
        {
            var dbConnectionString =
                configuration.GetRequiredSection("WebPhoneDbConnectionString").Value
                ?? throw new InvalidOperationException(
                    "Postgres connection string is not configured."
                );

            return new DbConnectionResolver(dbConnectionString);
        });

        services.AddScoped<MessagesReader>();
        services.AddScoped<ProfileSettingsRepository>();
        services.AddScoped<ContactSettingsRepository>();
        services.AddScoped<PushSubscriptionsRepository>();
        services.AddScoped<PushNotifier>();

        services.AddSingleton(new RtcNegotiationStore());
       
        services.AddSingleton<IMessagesWriter, HubMessagesChannel>();
        services.AddSingleton<DbMessagesWriter>();
        services.AddSingleton<ConnectedUsersStorage>();

        services.AddScoped<RtcMatchMaker>();

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

        services.AddSignalRConnectionsSupport();

        return services;
    }

    public static IServiceCollection AddSignalRConnectionsSupport(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<IUserIdProvider, PeerUserProvider>();
        return services;
    }
}

