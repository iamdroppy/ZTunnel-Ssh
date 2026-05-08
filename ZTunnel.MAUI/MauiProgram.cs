using Microsoft.Extensions.Logging;
using ZTunnel.MAUI.Services;

namespace ZTunnel.MAUI
{
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
            // Register the tunnel service ONCE as a singleton, then point the
            // hosted-service registration at the same instance. Without this
            // the DI container would create two separate SshTunnelService
            // instances — the hosted one would auto-connect on startup, but
            // the UI would inject the *other* one and show nothing connected.
            builder.Services.AddSingleton<TunnelConfigStore>();
            builder.Services.AddSingleton<SshTunnelService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<SshTunnelService>());

            builder.Services.AddMauiBlazorWebView();

#if DEBUG
    		builder.Services.AddBlazorWebViewDeveloperTools();
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
