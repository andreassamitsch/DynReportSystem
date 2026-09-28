# DynReport architecture foundation before mass RDL migration

## Rule

Do not migrate the remaining SSRS inventory at scale until the native report model and
runtime boundaries are stable.

RDL is only an analysis source. Each report is deliberately redesigned as a native
DynReport report.

## P0 - foundation

- central SQL metadata schema
- immutable revisions + active revision pointer
- logical DataSource registry
- no secrets in packages
- package upload hardening
- declarative expression-only runtime
- SQL query policy + least-privilege DB identities
- execution IDs and structured audit
- cancellation, timeouts, row limits
- schema/version contract
- runtime/designer use the same model

## Completed through 0.11.0

- central metadata schema and immutable SQL revisions
- package/runtime security guardrails and least-privilege design
- execution limits, audit, metrics and health/readiness endpoints
- portal/package/ACL hot-path performance work
- SQL revision browsing and permission-gated restore
- SHA-256/length validation before restore
- transactional active-revision + ACL rollback with local package compensation
- serialized package mutations
- backup/recovery and SQL performance runbooks
- renderer-neutral semantic chart/table model and designer editors
- zero-license-cost renderer policy (ECharts + native table + optional future Tabulator)
- server-side interactive table query contract
- SQL-backed paging, global search, typed column filters and sorting
- stable row grouping order in server mode
- authenticated/authorized table query JSON endpoint for future client-heavy renderers
- interactive table telemetry and bounded page size

## P1 - product readiness

- validate 0.7.1 rollback on APP-01 before making SQL the authoritative read path
- server-side aggregate grouping / expandable groups
- semantic pivot contract and server-side pivot execution
- optional MIT Tabulator heavy-data renderer using the existing table query API
- static/lightweight portal
- dataset cache with security-scoped keys
- background worker/job queue
- Excel/PDF worker exports
- health/readiness endpoints
- execution telemetry and slow-query diagnostics
- production Query Store rollout/thresholds after DBA review
- automated restore drill and documented RPO/RTO

## P2 - designer growth

- drag/drop grid
- property editor
- visual expression builder
- dataset editor with permission separation
- preview/sample data mode
- visual revision compare (restore is implemented)
- publish workflow
- component library expansion only through generic schema components

## RDL migration method

For every source report:

1. identify business question and users/devices
2. extract datasets/queries and parameter semantics
3. understand groups, calculations, custom code, colors, visibility and actions
4. measure query performance and worst-case parameters
5. redesign the UX intentionally
6. model it only with native generic DynReport components/expressions
7. compare values with SSRS
8. publish native revision
9. retire the RDL dependency for that report
