using WebPhone.Backend.Services;

namespace WebPhone.Api;

public static class SignalRHubEndpoints
{
    public static void MapSignalRHubEndpoints(this WebApplication app) { app.MapHub<SignallingHub>("/hub"); }
}
