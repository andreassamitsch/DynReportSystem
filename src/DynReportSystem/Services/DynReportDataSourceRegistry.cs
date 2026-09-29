using DynReportSystem.Models;
using Microsoft.Data.SqlClient;

namespace DynReportSystem.Services;

public sealed class DynReportDataSourceRegistry(
    IConfiguration config,
    ILogger<DynReportDataSourceRegistry> logger)
{
    public string ResolveConnectionString(DynReportDataSource source)
    {
        var direct = config[$"DataSources:{source.ConfigKey}:ConnectionString"];
        if (string.IsNullOrWhiteSpace(direct))
            direct = config[$"DataSources:{source.Id}:ConnectionString"];

        if (!string.IsNullOrWhiteSpace(direct))
            return ValidateServerConnection(source.Id, direct);

        // Backward compatibility for installations that already ran the
        // historic Kundencockpit before the central DataSources registry
        // existed. OxaionPRD is the native name for that same SQL target.
        if ((source.Id.Equals("OxaionPRD", StringComparison.OrdinalIgnoreCase)
             || source.ConfigKey.Equals("OxaionPRD", StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(config["Cockpit:ConnectionString"]))
        {
            logger.LogInformation(
                "DataSource {DataSourceId} uses legacy Cockpit:ConnectionString fallback.",
                source.Id);

            return ValidateServerConnection(
                source.Id,
                config["Cockpit:ConnectionString"]!);
        }

        // Transitional compatibility only. Native packages must not contain
        // credentials. A package may carry non-secret server/database target
        // metadata while APP-01 still migrates to the central DataSource registry.
        if (!string.IsNullOrWhiteSpace(source.DefaultConnectionString))
        {
            var allowLegacy = config.GetValue(
                "Security:AllowLegacyPackageConnectionStringFallback",
                true);

            if (!allowLegacy)
                throw new InvalidOperationException(
                    $"Datenquelle '{source.Id}' ist zentral nicht konfiguriert.");

            var target = new SqlConnectionStringBuilder(source.DefaultConnectionString);

            if (!string.IsNullOrWhiteSpace(target.Password)
                || !string.IsNullOrWhiteSpace(target.UserID))
            {
                throw new InvalidDataException(
                    $"Datenquelle '{source.Id}' enthält Zugangsdaten im Report-Paket. " +
                    "Das ist in nativen DynReport-Berichten nicht zulässig.");
            }

            var fallback = config["Cockpit:ConnectionString"];
            if (string.IsNullOrWhiteSpace(fallback))
                return ValidateServerConnection(source.Id, target.ConnectionString);

            var credentials = new SqlConnectionStringBuilder(fallback);
            if (credentials.IntegratedSecurity)
            {
                target.IntegratedSecurity = true;
            }
            else
            {
                target.UserID = credentials.UserID;
                target.Password = credentials.Password;
            }

            target.Encrypt = credentials.Encrypt;
            target.TrustServerCertificate = credentials.TrustServerCertificate;
            target.ConnectTimeout = credentials.ConnectTimeout;
            target.ApplicationName = "DynReport";

            logger.LogWarning(
                "DataSource {DataSourceId} uses transitional package target metadata. " +
                "Move the full target configuration to DataSources:{ConfigKey}.",
                source.Id,
                source.ConfigKey);

            return ValidateServerConnection(source.Id, target.ConnectionString);
        }

        throw new InvalidOperationException(
            $"Datenquelle '{source.Id}' ist nicht konfiguriert.");
    }

    private string ValidateServerConnection(string dataSourceId, string value)
    {
        var builder = new SqlConnectionStringBuilder(value)
        {
            ApplicationName = "DynReport"
        };

        var requireTlsValidation = config.GetValue(
            "Security:RequireValidatedSqlTls",
            false);

        if (!builder.Encrypt)
        {
            var message =
                $"Datenquelle '{dataSourceId}' verwendet keine SQL-Transportverschlüsselung.";

            if (requireTlsValidation)
                throw new InvalidOperationException(message);

            logger.LogWarning(message);
        }

        if (builder.TrustServerCertificate)
        {
            var message =
                $"Datenquelle '{dataSourceId}' verwendet TrustServerCertificate=True. " +
                "Für das Zielsystem ist Zertifikatsprüfung vorgesehen.";

            if (requireTlsValidation)
                throw new InvalidOperationException(message);

            logger.LogWarning(message);
        }

        return builder.ConnectionString;
    }
}
