using DynReportSystem.Components;
using DynReportSystem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.IISIntegration;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("DynReport System requires Windows/IIS for this SSO pilot.");

builder.Services.AddAuthentication(IISDefaults.AuthenticationScheme);
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
builder.Services.AddSingleton<DynReportMetadataStore>();
builder.Services.AddSingleton<DynReportDataSourceRegistry>();
builder.Services.AddSingleton<DynReportSqlPolicyValidator>();
builder.Services.AddSingleton<DynReportExecutionGate>();
builder.Services.AddSingleton<DynReportMetrics>();
builder.Services.AddSingleton<DynReportPackageStore>();
builder.Services.AddSingleton<DynExpressionEngine>();
builder.Services.AddScoped<DynReportPackageExecutionService>();
builder.Services.AddHealthChecks()
    .AddCheck<DynReportReadinessHealthCheck>("dynreport_ready");
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
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    ctx.Response.Headers["X-Correlation-ID"] = ctx.TraceIdentifier;

    // Start in report-only mode so the policy can be hardened without breaking
    // existing Blazor/ECharts behavior on APP-01.
    ctx.Response.Headers["Content-Security-Policy-Report-Only"] =
        "default-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'self'; " +
        "img-src 'self' data:; font-src 'self' data:; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self'; connect-src 'self' https: wss:; form-action 'self'";

    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/health/live", () => Results.Ok(new
{
    status = "ok",
    service = "DynReportSystem",
    process = Process.GetCurrentProcess().ProcessName,
    hosting = "IIS out-of-process / Kestrel"
})).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Name == "dynreport_ready"
}).AllowAnonymous();

app.MapGet("/api/packages/{reportId}/download",
    async (
        string reportId,
        HttpContext context,
        DynReportPackageStore packages,
        DynReportMetadataStore metadata,
        FolderAccess access) =>
    {
        var canExportDefinition =
            access.Can(context.User, reportId, "EditLayout")
            || access.Can(context.User, reportId, "Edit")
            || access.Can(context.User, reportId, "Publish")
            || access.Can(context.User, reportId, "Manage");

        if (!canExportDefinition)
            return Results.Forbid();

        var package = packages.Get(reportId);
        if (package is null || !File.Exists(package.FilePath))
            return Results.NotFound();

        await metadata.WriteAuditAsync(
            new DynAuditEvent(
                Guid.NewGuid(),
                "report.package.export",
                "success",
                context.User.Identity?.Name,
                reportId,
                DetailsJson: DynAuditEvent.Details(new
                {
                    package.Manifest.Version,
                    package.Manifest.SchemaVersion
                })),
            context.RequestAborted);

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

var metadataStore = app.Services.GetRequiredService<DynReportMetadataStore>();
if (metadataStore.IsConfigured)
{
    try
    {
        var packageStore = app.Services.GetRequiredService<DynReportPackageStore>();
        var migration = await packageStore.MigrateLocalRevisionsToMetadataAsync();

        if (!migration.CompletedPreviously)
        {
            app.Logger.LogInformation(
                "Local DynReport revision migration completed. Reports={Reports}, Imported={Imported}, Duplicates={Duplicates}, Failed={Failed}",
                migration.Reports,
                migration.Imported,
                migration.Duplicates,
                migration.Failed);
        }

        if (migration.Failed > 0)
        {
            app.Logger.LogWarning(
                "Local DynReport revision migration has {Failed} failed file(s). The migration marker was not written and the migration will retry on the next restart.",
                migration.Failed);
        }
    }
    catch (Exception ex)
    {
        // Do not make the web process unavailable because an optional historical
        // backfill failed. Readiness still reports SQL availability, and the
        // idempotent migration retries on the next restart until it succeeds.
        app.Logger.LogError(
            ex,
            "Local DynReport revision migration failed and will retry on the next restart.");
    }
}

app.Run();

public partial class Program { }
