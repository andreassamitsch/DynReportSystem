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
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
{
    // Mobile browsers suspend tabs and WLAN changes can interrupt SignalR briefly.
    // Retain disconnected circuits longer so the existing report state can rejoin.
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(10);
    options.DisconnectedCircuitMaxRetained = 250;
});
builder.Services.AddSingleton<FolderAccess>();
builder.Services.AddSingleton<RdlCatalog>();
builder.Services.AddSingleton<RdlStyleCatalog>();
builder.Services.AddSingleton<DashboardCatalog>();
builder.Services.AddSingleton<ChartOptionFactory>();
builder.Services.AddSingleton<ImportedPortalCatalog>();
builder.Services.AddSingleton<DynReportPackageStore>();
builder.Services.AddSingleton<DynExpressionEngine>();
builder.Services.AddScoped<DynReportPackageExecutionService>();
builder.Services.AddSingleton<CustomReportStore>();
builder.Services.AddSingleton<ProductionTvReportService>();
builder.Services.AddSingleton<DynamicRdlService>();
builder.Services.AddSingleton<RdlPresentationService>();
builder.Services.AddSingleton<RdlExpressionEvaluator>();
builder.Services.AddSingleton<RdlPresentationRenderer>();
builder.Services.AddSingleton<DynamicVisualizationService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<DynamicReportExecutionService>();

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

app.MapGet("/api/packages/{reportId}/download",
    (string reportId, HttpContext context, DynReportPackageStore packages, FolderAccess access) =>
    {
        if (!access.Can(context.User, reportId, "View"))
            return Results.Forbid();

        var package = packages.Get(reportId);
        if (package is null || !File.Exists(package.FilePath))
            return Results.NotFound();

        var safeTitle = string.Concat(package.Manifest.Title.Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

        return Results.File(
            package.FilePath,
            "application/vnd.dynreport+zip",
            $"{safeTitle}.dynreport");
    })
    .RequireAuthorization();

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode()
   .RequireAuthorization();

app.Run();

public partial class Program { }
