# ADR-0004: Web/runtime/designer architecture

- **Status:** Accepted
- **Date:** 2026-09-27

## Decision

DynReport remains a modular ASP.NET Core application, not a microservice estate.

The target web architecture is hybrid:

- **Portal/catalog:** static/server-rendered where practical, minimal persistent UI state
- **Viewer:** server-side authorized APIs with client-side paging/rendering
- **Designer:** client-heavy editor using the same declarative report schema as runtime
- **SQL/auth/secrets:** server-side only
- **Worker:** separately deployable process for long exports, scheduled reports and
  materialization

Current Interactive Server pages are a transition implementation, not the final scalability
boundary.

## Module boundaries

- Catalog
- Package/Revision Management
- Authorization
- Query Runtime
- Expression Runtime
- Layout/Visual Runtime
- Designer
- Export
- Audit/Telemetry
- Background Jobs

No report-specific Razor/C# renderer is permitted for native reports.

## Performance principles

- no filesystem/package scans on render hot paths
- no N+1 catalog or permission lookups
- bounded query concurrency
- typed parameters
- short-lived pooled SQL connections
- server-side aggregation where possible
- paging/streaming for large result sets
- long-running export work outside request/circuit lifetime
- security-scoped cache keys
- cancellation propagation
- timeout and row/byte limits
