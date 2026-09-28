# APP-01: DynReport metadata database setup

The metadata database is optional during the 0.7 transition, but it is the target
production catalog.

## 1. Create a dedicated database

Recommended name:

```text
DynReport
```

Create it through the normal DBA process so recovery model, files, backup targets and
maintenance match the existing SQL Server standards.

Do not place DynReport metadata tables inside the Oxaion or Syncos application schemas.

## 2. Apply the schema

For a new database, run these scripts in order:

```text
sql/001-dynreport-metadata-schema.sql
sql/002-fix-audit-and-reportgrant-index.sql
```

against the new `DynReport` database.

If the original 0.7.1 `001` script was already executed and produced the
`RowCount` syntax error / ReportGrant 1260-byte clustered-key warning, **do not
drop the database**. Run only:

```text
sql/002-fix-audit-and-reportgrant-index.sql
```

The repair migration is idempotent and completes the missing `dyn.AuditEvent`
table while replacing the over-wide clustered ReportGrant primary key with a
small surrogate clustered key plus a logical unique nonclustered index.

Verify:

```sql
SELECT *
FROM dyn.SchemaVersion
ORDER BY VersionNumber DESC;
```

Version 2 must exist.

## 3. Runtime identity

Prefer a dedicated Windows service identity/gMSA for the DynReport IIS application pool
when the infrastructure supports it.

Use `sql/010-runtime-principal-template.sql` as a reviewed template.

The runtime identity must not be:

- sysadmin
- db_owner on Syncos/Oxaion
- db_datawriter on source systems

Expose reporting views and/or approved stored procedures and grant only the required
SELECT/EXECUTE rights.

## 4. SQL transport security

Target production configuration:

```text
Integrated Security=True
Encrypt=True
TrustServerCertificate=False
Application Name=DynReport
```

This requires a SQL Server certificate that the APP-01 machine trusts.

During rollout, `Security:RequireValidatedSqlTls` is false by default so the existing
environment is not broken unexpectedly. After the SQL certificate path is verified,
enable it.

## 5. Configure APP-01

In:

```text
C:\Program Files\DynReportSystem\appsettings.Production.json
```

configure either:

```json
{
  "Metadata": {
    "ConnectionString": "Server=...;Database=DynReport;Integrated Security=True;Encrypt=True;TrustServerCertificate=False;Application Name=DynReport;"
  }
}
```

or:

```json
{
  "DataSources": {
    "DynReportMetadata": {
      "ConnectionString": "Server=...;Database=DynReport;Integrated Security=True;Encrypt=True;TrustServerCertificate=False;Application Name=DynReport;"
    }
  }
}
```

Do not commit the production configuration.

DynReport uses the first non-empty value from `Metadata:ConnectionString` and
`DataSources:DynReportMetadata:ConnectionString`.

After the metadata database has been created, permissions and TLS have been validated, set:

```json
{
  "Metadata": {
    "FailPublishWhenUnavailable": true
  }
}
```

for the production rollout. This prevents a native report publish from silently advancing the
local filesystem package while the central metadata/revision catalog is unavailable. Keep it
`false` only during the deliberate transition/bootstrap phase.

## 6. Restart and verify

Recycle the DynReport IIS application pool, then test:

```text
https://reports.fuchshofer.at/health/live
https://reports.fuchshofer.at/health/ready
```

After importing or saving a native report, verify:

```sql
SELECT ReportId, Title, ActiveRevisionId, ModifiedUtc
FROM dyn.Report
ORDER BY ModifiedUtc DESC;

SELECT TOP (20)
    ReportId,
    PackageVersion,
    FormatVersion,
    ContentHash,
    PackageLength,
    CreatedUtc,
    CreatedBy
FROM dyn.ReportRevision
ORDER BY CreatedUtc DESC;
```

## Transition behavior in 0.7

The local package store remains the active runtime source so existing installations keep
working. When the metadata DB is configured, validated publishes are mirrored into SQL.

A later architecture milestone will switch catalog/active-revision resolution to SQL as the
authoritative source after restore/rollback and concurrency behavior have been validated.
