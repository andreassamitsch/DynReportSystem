using DynReportSystem.Components;
using DynReportSystem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.IIS;

var builder = WebApplication.CreateBuilder(args);

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("DynReport System requires Windows/IIS for this SSO pilot.");

builder.Services.AddAuthentication(IISServerDefaults.AuthenticationScheme);
builder.Services.AddAuthorization(options =>
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<FolderAccess>();
builder.Services.AddSingleton<RdlCatalog>();
builder.Services.AddScoped<ReportService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Cache-Control"] = "no-store";
    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode()
   .RequireAuthorization();

app.Run();

public partial class Program { }
