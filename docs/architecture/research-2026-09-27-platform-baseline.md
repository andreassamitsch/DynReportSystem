# DynReport platform research baseline — 2026-09-27

This document records the architecture conclusions from the production-readiness research before
mass SSRS migration. It is the rationale behind the 0.7 architecture work and should be treated as
the baseline when future implementation choices are reviewed.

## Executive decision

DynReport is not an RDL-compatible replacement engine.

RDL remains a **one-time analysis source** for existing reports. Each important report is analysed,
validated and deliberately rebuilt as a native, declarative DynReport definition. The target
runtime format is the versioned `.dynreport` contract used by both runtime and designer.

The production platform remains a modular ASP.NET Core application behind IIS/HTTPS with Windows
SSO. SQL Server is the durable system of record for DynReport catalog state; the existing Oxaion,
Syncos, staging and FU databases remain source systems and are not used as DynReport metadata
schemas.

## Target architecture

### Web tier

- IIS terminates HTTPS and provides Windows Authentication.
- ASP.NET Core owns authorization, report APIs, query orchestration, audit and package management.
- The catalog should become progressively lighter and must not depend on long-lived Blazor server
  circuits for high-volume data delivery.
- The viewer moves toward server-authorized paging/streaming APIs with client-side rendering.
- The designer is client-heavy, but edits exactly the same declarative schema the runtime executes.
- Long exports, schedules and materialization belong in a separately deployable worker rather than
  inside a web request or UI circuit.

This is intentionally **not** a microservice estate. Clear module boundaries are preferred over
operational complexity.

### Metadata and revision storage

A dedicated SQL database named `DynReport` is the long-term authoritative store for:

- report catalog metadata
- immutable report revisions
- the active revision pointer
- report grants
- logical datasource profiles
- audit events
- execution history
- background jobs
- schema migration history

`.dynreport` remains the portable import/export/build artifact. It is not the only production
database.

During the transition, `C:\ProgramData\DynReportSystem\Packages` remains the active runtime source
so existing installations continue to work. New architecture must reduce, not deepen, this
filesystem dependency.

### Source data

Report packages reference logical datasource IDs such as `OxaionPRD` or `SyncosPRD`.
Credentials and secrets are server-side only.

Preferred production access is a dedicated Windows service identity/gMSA with the least privileges
required on explicitly exposed reporting views and approved stored procedures.

The DynReport runtime identity must not be sysadmin, db_owner on Oxaion/Syncos, or a broad writer on
source systems.

## Main failure modes identified

### Filesystem as the only catalog

A package-directory-only catalog becomes fragile when reports, revisions, permissions, backups,
multiple web nodes and concurrent editing grow. It also makes transactional activation and auditing
hard.

**Decision:** move durable catalog/revision state to SQL in phases.

### Automatic RDL presentation conversion

A generic RDL renderer can preserve enough structure for migration inspection, but produces a poor
long-term UX and keeps the new platform coupled to SSRS concepts.

**Decision:** no new architecture may depend on automatic RDL-to-DynReport conversion.

### Report-specific code

One Razor/C# implementation per report would make hundreds of reports unmaintainable and would make
a real designer impossible.

**Decision:** report behavior must be representable by generic schema components and declarative
expressions.

### Query storms and oversized results

TV refreshes, simultaneous users and broad parameters can overload both APP-01 and SQL Server.
Materializing large results in a Blazor circuit is especially expensive.

**Decision:** bounded global query concurrency, cancellation, absolute timeout/row/byte limits,
server-side aggregation, refresh jitter, paging/streaming and a worker for long work.

### Authorization N+1 work

Repeated AD group checks, package scans and per-card ACL traversal make the portal slow even before
a report query runs.

**Decision:** index/cache catalog and permission inputs and invalidate them only on actual changes.

### SQL text as a security boundary

Static SQL inspection can be bypassed by complex constructs and is not a sufficient trust boundary.

**Decision:** the database principal is the primary boundary. Static query policy is defense in
depth only. End-user values remain typed SQL parameters.

### Embedded credentials

Portable report files, Git and CI artifacts are inappropriate secret stores.

**Decision:** no credentials in packages or source control. Production secrets stay in protected
server configuration / the approved secret recovery process.

### Long work in the web process

Excel/PDF exports, schedules and precomputation can outlive request/circuit lifetimes and consume
web capacity.

**Decision:** durable SQL-backed job state plus a separately deployable worker.

### Incomplete backup strategy

Backing up only the web directory or only the metadata database does not restore a working report
system.

**Decision:** restore tests cover metadata DB, immutable package content, production configuration,
secret recovery and TLS certificates.

## Performance baseline

The runtime must preserve these guardrails:

- no package-directory scan on render/search/ACL hot paths
- no N+1 permission/catalog lookup
- pooled short-lived SQL connections
- typed parameters
- bounded query concurrency
- per-query cancellation and timeouts
- absolute row and approximate result-byte limits
- server-side aggregation where practical
- paging/streaming for large viewer tables
- refresh jitter for automatic dashboards
- slow-query telemetry with report/dataset IDs
- SQL Query Store used as the database-side performance history after DBA review
- security-scoped cache keys; never share cached data across authorization scopes accidentally

A fast portal and a fast database query are separate concerns and must be measured separately.

## Security baseline

- Windows SSO through IIS
- explicit server-side report authorization
- least-privilege Windows/SQL service identity
- encrypted SQL transport with certificate validation in production
- no arbitrary C#, VB, JavaScript, PowerShell or shell from report definitions
- no arbitrary filesystem/network access from expressions
- package ZIP hardening and size/compression limits
- privileged separation for dataset editing and publishing
- immutable revision history and audit trail
- security headers and CSP rollout
- production configuration and source-system SQL kept out of the public repository

## Backup and recovery baseline

A usable restore consists of:

1. restore the DynReport metadata database
2. restore immutable package/revision content if stored outside SQL
3. restore APP-01 production configuration
4. recover/re-bind secrets and certificates
5. start the application against the restored metadata
6. verify Windows SSO and ACL behavior
7. execute at least one Oxaion and one Syncos report
8. verify audit writes
9. restore a prior report revision and verify the active result

RPO, RTO and retention values are operational decisions and must be agreed rather than guessed in
application code.

## Deployment/update baseline

- application binaries are replaceable; report and production state are preserved separately
- database schema changes are versioned and idempotent where practical
- updates must preserve `appsettings.Production.json`, ACL state and package/revision storage
- health endpoints distinguish process liveness from dependency readiness
- rollback must be possible for both application deployment and report revision activation
- schema/runtime compatibility is explicit through package schema and minimum runtime version

## Implementation status

### Implemented by 0.7.0

- self-contained native `.dynreport` schema/runtime/designer contract
- dedicated SQL metadata schema and immutable SQL revision storage
- validated package mirroring into SQL
- logical datasource registry
- credential rejection in packages
- read-only SQL policy validation as defense in depth
- package ZIP limits
- query concurrency, cancellation, timeout, row and result-memory limits
- execution/audit metadata
- health/readiness endpoints
- portal/package/ACL hot-path caching improvements
- refresh jitter
- OpenTelemetry-compatible .NET metrics
- optimistic designer concurrency protection
- security header/CSP report-only baseline
- APP-01 SQL setup and backup/recovery documentation

### Added in 0.7.1

- SQL revision browsing in the designer
- permission-gated two-step report revision rollback
- SHA-256 and package-length verification before restore
- transactional SQL activation of revision, metadata and ACL state
- compensating local-package rollback if SQL activation fails
- serialized package import/designer-save/restore mutations

### Deliberately still open

- switch catalog/active-revision reads from filesystem to SQL as the authoritative runtime path
- server-authorized paged/streamed viewer API
- security-scoped result caching
- durable background worker for long exports/schedules/materialization
- Excel/PDF worker exports
- production Query Store rollout and alert thresholds
- enforced CSP after compatibility validation
- automated restore drill
- multi-node/shared-cache behavior if horizontal scale is ever required

The SQL-authoritative read switch should happen only after revision rollback and restore behavior has
been validated on APP-01.
