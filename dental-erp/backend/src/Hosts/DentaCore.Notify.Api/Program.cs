using DentaCore.BuildingBlocks.Infrastructure.Observability;
using DentaCore.BuildingBlocks.Infrastructure.Auth;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Notify.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDentaCoreObservability(builder.Configuration, builder.Logging, "notify-api");

builder.Services.AddNotifyModule(builder.Configuration);
builder.Services.AddJwtValidation(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<TenantResolutionMiddleware>();
app.UseAuthentication();
app.UseMiddleware<TenantClaimGuardMiddleware>();
app.UseAuthorization();

app.MapDentaCoreHealth();
app.MapHub<NotificationHub>(NotificationHub.Path, options =>
{
    options.CloseOnAuthenticationExpiration = true;   // token bitən kimi bağlantı bağlanır, klient yeni token ilə qoşulur
});

await app.RunAsync();

public partial class Program;
