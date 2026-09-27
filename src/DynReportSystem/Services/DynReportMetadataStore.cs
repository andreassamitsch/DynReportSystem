using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DynReportSystem.Models;
using Microsoft.Data.SqlClient;

namespace DynReportSystem.Services;

/// <summary>
/// Transitional SQL-backed metadata store. The application can run without it,
/// but when Metadata:ConnectionString is configured every package publish and
/// execution can be registered centrally. This is the foundation for moving
/// the catalog/active-revision pointer fully into SQL Server.
/// </summary>
public sealed class DynReportMetadataStore(
    IConfiguration config,
    ILogger<DynReportMetadataStore> logger)
{
    private readonly string _connectionString =
        config["Metadata:ConnectionString"]
        ?? config["DataSources:DynReportMetadata:ConnectionString"]
        ?? "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    public async Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return true;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(
            "SELECT TOP (1) VersionNumber FROM dyn.SchemaVersion ORDER BY VersionNumber DESC;",
            connection)
        {
            CommandTimeout = 5
        };

        _ = await command.ExecuteScalarAsync(cancellationToken);
        return true;
    }

    public async Task<Guid?> PublishPackageAsync(
        LoadedDynReportPackage package,
        ReadOnlyMemory<byte> packageBytes,
        string? userName,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return null;

        var hash = Convert.ToHexString(SHA256.HashData(packageBytes.Span)).ToLowerInvariant();
        var revisionId = Guid.NewGuid();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await UpsertReportAsync(connection, transaction, package.Manifest, cancellationToken);

            var existingRevision = await FindRevisionByHashAsync(
                connection,
                transaction,
                package.Manifest.ReportId,
                hash,
                cancellationToken);

            if (existingRevision.HasValue)
            {
                revisionId = existingRevision.Value;
            }
            else
            {
                await InsertRevisionAsync(
                    connection,
                    transaction,
                    revisionId,
                    package,
                    packageBytes,
                    hash,
                    userName,
                    cancellationToken);
            }

            await ReplaceGrantsAsync(
                connection,
                transaction,
                package.Manifest,
                cancellationToken);

            await SetActiveRevisionAsync(
                connection,
                transaction,
                package.Manifest.ReportId,
                revisionId,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Registered DynReport {ReportId} revision {RevisionId} in metadata database",
                package.Manifest.ReportId,
                revisionId);

            return revisionId;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task WriteAuditAsync(
        DynAuditEvent audit,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return;

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            const string sql = """
                INSERT INTO dyn.AuditEvent
                (
                    CorrelationId, UserName, EventType, ReportId, RevisionId,
                    DataSourceId, DatasetId, DurationMs, RowCount, Result, DetailsJson
                )
                VALUES
                (
                    @CorrelationId, @UserName, @EventType, @ReportId, @RevisionId,
                    @DataSourceId, @DatasetId, @DurationMs, @RowCount, @Result, @DetailsJson
                );
                """;

            await using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = 10
            };

            command.Parameters.AddWithValue("@CorrelationId", audit.CorrelationId);
            command.Parameters.AddWithValue("@UserName", Db(audit.UserName));
            command.Parameters.AddWithValue("@EventType", audit.EventType);
            command.Parameters.AddWithValue("@ReportId", Db(audit.ReportId));
            command.Parameters.AddWithValue("@RevisionId", audit.RevisionId.HasValue
                ? audit.RevisionId.Value
                : DBNull.Value);
            command.Parameters.AddWithValue("@DataSourceId", Db(audit.DataSourceId));
            command.Parameters.AddWithValue("@DatasetId", Db(audit.DatasetId));
            command.Parameters.AddWithValue("@DurationMs", audit.DurationMs.HasValue
                ? audit.DurationMs.Value
                : DBNull.Value);
            command.Parameters.AddWithValue("@RowCount", audit.RowCount.HasValue
                ? audit.RowCount.Value
                : DBNull.Value);
            command.Parameters.AddWithValue("@Result", audit.Result);
            command.Parameters.AddWithValue("@DetailsJson",
                string.IsNullOrWhiteSpace(audit.DetailsJson)
                    ? DBNull.Value
                    : audit.DetailsJson);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Auditing must never leak report data or break an otherwise valid
            // report execution during the transition phase.
            logger.LogError(ex, "Could not write DynReport audit event {EventType}", audit.EventType);
        }
    }

    public async Task RegisterExecutionStartAsync(
        Guid executionId,
        Guid correlationId,
        string reportId,
        string? userName,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return;

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            INSERT INTO dyn.ReportExecution
            (
                ExecutionId, CorrelationId, ReportId, UserName,
                StartedUtc, Result, ErrorCount
            )
            VALUES
            (
                @ExecutionId, @CorrelationId, @ReportId, @UserName,
                SYSUTCDATETIME(), N'running', 0
            );
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 10 };
        command.Parameters.AddWithValue("@ExecutionId", executionId);
        command.Parameters.AddWithValue("@CorrelationId", correlationId);
        command.Parameters.AddWithValue("@ReportId", reportId);
        command.Parameters.AddWithValue("@UserName", Db(userName));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RegisterExecutionEndAsync(
        Guid executionId,
        long durationMs,
        int errorCount,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return;

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            const string sql = """
                UPDATE dyn.ReportExecution
                SET
                    CompletedUtc = SYSUTCDATETIME(),
                    DurationMs = @DurationMs,
                    ErrorCount = @ErrorCount,
                    Result = CASE WHEN @ErrorCount = 0 THEN N'success' ELSE N'partial' END
                WHERE ExecutionId = @ExecutionId;
                """;

            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 10 };
            command.Parameters.AddWithValue("@ExecutionId", executionId);
            command.Parameters.AddWithValue("@DurationMs", durationMs);
            command.Parameters.AddWithValue("@ErrorCount", errorCount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not finalize DynReport execution {ExecutionId}", executionId);
        }
    }

    private static async Task UpsertReportAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DynReportManifest manifest,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dyn.Report
            SET
                [Path] = @Path,
                FolderPath = @FolderPath,
                Title = @Title,
                [Description] = @Description,
                IsDeleted = 0,
                ModifiedUtc = SYSUTCDATETIME()
            WHERE ReportId = @ReportId;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO dyn.Report
                (ReportId, [Path], FolderPath, Title, [Description])
                VALUES
                (@ReportId, @Path, @FolderPath, @Title, @Description);
            END;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@ReportId", manifest.ReportId);
        command.Parameters.AddWithValue("@Path", manifest.Path);
        command.Parameters.AddWithValue("@FolderPath", manifest.FolderPath);
        command.Parameters.AddWithValue("@Title", manifest.Title);
        command.Parameters.AddWithValue("@Description", Db(manifest.Description));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid?> FindRevisionByHashAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string reportId,
        string hash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT RevisionId
            FROM dyn.ReportRevision
            WHERE ReportId = @ReportId
              AND ContentHash = @ContentHash;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@ReportId", reportId);
        command.Parameters.AddWithValue("@ContentHash", hash);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    private static async Task InsertRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid revisionId,
        LoadedDynReportPackage package,
        ReadOnlyMemory<byte> packageBytes,
        string hash,
        string? userName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dyn.ReportRevision
            (
                RevisionId, ReportId, PackageVersion, FormatVersion,
                ContentHash, PackageContent, PackageLength,
                CreatedBy, IsValidated, ValidationSummary
            )
            VALUES
            (
                @RevisionId, @ReportId, @PackageVersion, @FormatVersion,
                @ContentHash, @PackageContent, @PackageLength,
                @CreatedBy, 1, N'Validated by DynReport runtime before publish'
            );
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@RevisionId", revisionId);
        command.Parameters.AddWithValue("@ReportId", package.Manifest.ReportId);
        command.Parameters.AddWithValue("@PackageVersion", package.Manifest.Version);
        command.Parameters.AddWithValue("@FormatVersion", package.Manifest.SchemaVersion);
        command.Parameters.AddWithValue("@ContentHash", hash);
        command.Parameters.Add("@PackageContent", System.Data.SqlDbType.VarBinary, -1).Value =
            packageBytes.ToArray();
        command.Parameters.AddWithValue("@PackageLength", packageBytes.Length);
        command.Parameters.AddWithValue("@CreatedBy", Db(userName));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceGrantsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        DynReportManifest manifest,
        CancellationToken cancellationToken)
    {
        await using (var delete = new SqlCommand(
            "DELETE FROM dyn.ReportGrant WHERE ReportId = @ReportId;",
            connection,
            transaction))
        {
            delete.Parameters.AddWithValue("@ReportId", manifest.ReportId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insertSql = """
            INSERT INTO dyn.ReportGrant
            (ReportId, PrincipalType, Principal, Permission)
            VALUES
            (@ReportId, @PrincipalType, @Principal, @Permission);
            """;

        foreach (var grant in manifest.Grants)
        {
            foreach (var permission in grant.Permissions.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await using var insert = new SqlCommand(insertSql, connection, transaction);
                insert.Parameters.AddWithValue("@ReportId", manifest.ReportId);
                insert.Parameters.AddWithValue("@PrincipalType", grant.PrincipalType);
                insert.Parameters.AddWithValue("@Principal", grant.Principal);
                insert.Parameters.AddWithValue("@Permission", permission);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task SetActiveRevisionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string reportId,
        Guid revisionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dyn.Report
            SET ActiveRevisionId = @RevisionId,
                ModifiedUtc = SYSUTCDATETIME()
            WHERE ReportId = @ReportId;
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@RevisionId", revisionId);
        command.Parameters.AddWithValue("@ReportId", reportId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object Db(string? value) =>
        string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
}

public sealed record DynAuditEvent(
    Guid CorrelationId,
    string EventType,
    string Result,
    string? UserName = null,
    string? ReportId = null,
    Guid? RevisionId = null,
    string? DataSourceId = null,
    string? DatasetId = null,
    long? DurationMs = null,
    long? RowCount = null,
    string? DetailsJson = null)
{
    public static string Details(object value) =>
        JsonSerializer.Serialize(value);
}
