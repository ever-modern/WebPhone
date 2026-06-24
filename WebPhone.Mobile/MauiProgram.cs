using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebPhone.Mobile.Services.Data;
using WebPhone.Services.Data;

namespace WebPhone.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        IServiceCollection services = builder.Services;
        services.AddMauiBlazorWebView();

        using var stream = FileSystem.OpenAppPackageFileAsync("appsettings.json").GetAwaiter().GetResult();
        builder.Configuration.AddJsonStream(stream);

        services.ConfigureWebPhoneFrontendApplication(builder.Configuration);
        services.AddSingleton<ILocalStore, MauiLocalStore>();
        //services.AddScoped<IRtcConnector, NativeRtcConnector>();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
