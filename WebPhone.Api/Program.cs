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
                policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()
            );
        });

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }
        
        app.UseCors();

        app.UseExceptionMapper();
        app.MapRestEndpoints();
        app.MapSignalRHubEndpoints();
        
        await app.RunAsync();
    }
}
