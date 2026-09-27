using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DynReportSystem.Services;

public sealed class DynReportReadinessHealthCheck(
    DynReportMetadataStore metadata,
    DynReportPackageStore packages) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(packages.RootPath);

            var probe = Path.Combine(packages.RootPath, ".health-probe");
            await File.WriteAllTextAsync(probe, "ok", cancellationToken);
            File.Delete(probe);

            if (metadata.IsConfigured)
                await metadata.CheckAsync(cancellationToken);

            return HealthCheckResult.Healthy(
                metadata.IsConfigured
                    ? "Package store writable; metadata database reachable."
                    : "Package store writable; metadata database not configured yet.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "DynReport readiness check failed.",
                ex);
        }
    }
}
