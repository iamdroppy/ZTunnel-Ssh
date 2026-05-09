using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
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

#if WINDOWS
            // Confirm-on-exit: intercept the WinUI window Close and prompt the user.
            // Without this, hitting the X (or Alt+F4) drops every SSH tunnel
            // and every port forward instantly — easy to do by accident.
            builder.ConfigureLifecycleEvents(events =>
            {
                events.AddWindows(windows =>
                {
                    windows.OnWindowCreated(window =>
                    {
                        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

                        // Captured per-window state — once the user confirms,
                        // we let the next Close go through unimpeded.
                        bool confirmedExit = false;
                        bool dialogOpen = false;

                        appWindow.Closing += async (sender, args) =>
                        {
                            if (confirmedExit) return;
                            args.Cancel = true;
                            if (dialogOpen) return;

                            dialogOpen = true;
                            try
                            {
                                var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                                {
                                    Title = "Exit ZTunnel?",
                                    Content = "All active SSH tunnels and port forwards will be closed.",
                                    PrimaryButtonText = "Exit",
                                    CloseButtonText = "Cancel",
                                    DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
                                    XamlRoot = (window.Content as Microsoft.UI.Xaml.FrameworkElement)?.XamlRoot
                                };

                                var result = await dialog.ShowAsync();
                                if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                                {
                                    confirmedExit = true;
                                    Microsoft.UI.Xaml.Application.Current.Exit();
                                }
                            }
                            catch
                            {
                                // If we can't show a dialog for any reason
                                // (e.g. no XamlRoot yet), fall back to exiting
                                // rather than trapping the user inside the app.
                                confirmedExit = true;
                                Microsoft.UI.Xaml.Application.Current.Exit();
                            }
                            finally
                            {
                                dialogOpen = false;
                            }
                        };
                    });
                });
            });
#endif
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
