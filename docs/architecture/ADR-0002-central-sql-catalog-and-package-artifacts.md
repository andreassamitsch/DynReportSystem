# ADR-0002: Central SQL catalog with portable .dynreport artifacts

- **Status:** Accepted
- **Date:** 2026-09-27
- **Supersedes:** local filesystem as the long-term production source of truth

## Decision

DynReport uses SQL Server as the long-term authoritative catalog for report metadata,
published revisions, access rules, audit and background-job state.

A `.dynreport` file remains the **portable import/export/build artifact**. It is not the
only production database.

The runtime is migrated in phases:

1. **Transition:** filesystem packages remain readable so current installations keep running.
2. **Metadata phase:** imports/designer saves also register an immutable revision in the
   DynReport metadata database.
3. **Authoritative phase:** active report/revision pointers are resolved from SQL Server.
4. **HA phase:** package bytes/revisions can be served from shared storage or SQL-backed
   revision storage; web nodes are stateless.

## Why

A central SQL catalog gives DynReport transactional revision activation, backup/restore,
auditing, indexing, concurrent editing protection and a natural location for jobs,
permissions and execution history.

A single file per report remains useful because it is portable, diffable when unpacked,
CI-buildable, easy to export, and independent of the runtime deployment.

## SQL database contents

The DynReport metadata database owns:

- Reports
- immutable ReportRevisions
- ActiveRevision pointer
- DataSourceProfiles (logical IDs only; no secrets)
- ReportGrants
- AuditEvents
- ReportExecutions
- BackgroundJobs
- schema migration history

## Not stored in report packages

- passwords
- SQL authentication secrets
- access tokens
- certificates/private keys
- machine-local absolute paths
- application code

## Backup rule

A successful database backup is not sufficient by itself. Restore tests must include:

1. DynReport metadata DB
2. active package revisions / package binary storage
3. server configuration
4. secret recovery procedure
5. TLS certificate recovery where applicable

## Migration impact

Existing package files under `C:\ProgramData\DynReportSystem\Packages` remain supported
during the transition. New architecture code must not deepen that filesystem dependency.
