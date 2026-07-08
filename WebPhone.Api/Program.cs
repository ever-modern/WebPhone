using WebPhone.Backend.Services;

namespace WebPhone.Api;

public partial class Program
{
    static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddOpenApi();
        builder.Services.ConfigureWebPhoneBackendServices(builder.Configuration);
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
                policy
                    .WithOrigins(
                        "https://web-phone.enjoyer-station.myvnc.com",
                        "https://web-phone.ever-modern.duckdns.org",
                        "https://localhost:7087",
                        "http://localhost:5108"
                    )
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()
            );
        });

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseRouting();

        app.UseCors();

        app.UseExceptionMapper();
        app.MapRestEndpoints();
        app.MapSignalRHubEndpoints();

        await app.RunAsync();
    }
}
