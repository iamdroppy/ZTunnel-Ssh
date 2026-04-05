using ZTunnel.Components;
using ZTunnel.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// SSH tunnel manager as a singleton so state persists across pages,
// and also register it as a hosted service so it runs in the background.
builder.Services.AddSingleton<SshTunnelService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SshTunnelService>());
builder.Services.AddSingleton<TunnelConfigStore>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
