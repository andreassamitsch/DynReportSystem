# ADR-0014: Page-scoped execution and parameter-aware dataset cache

- **Status:** Accepted
- **Date:** 2026-09-29
- **Applies to:** native DynReport execution runtime

## Context

Large analytical reports such as the Kundencockpit contain summary charts, KPIs, server-paged detail tables and expensive analytical datasets. Executing every dataset whenever any parameter changes causes unnecessary SQL load and makes the native runtime slower than SSRS.

Different datasets often depend on different parameter subsets. Example: the Lager/Kundenartikel analysis depends on customer and salesperson, but not the selected date range.

## Decision

DynReport uses page-scoped, dependency-aware execution.

### Page-scoped execution

Only datasets required by the active native report page are executed.

When the user opens another page, only datasets required by that page and not already available with the current parameter signature are loaded.

Server-mode tables remain deferred and execute their paged query only when their component is rendered.

### Parameter-aware cache

Each open report instance keeps an in-memory dataset cache.

The cache key consists of:

- package version
- dataset ID
- only the report parameters explicitly bound by that dataset

A dataset is therefore reused when unrelated report parameters change.

Examples:

- changing Von/Bis does not invalidate a dataset that only binds Kunde / Vertriebsmitarbeiter
- changing Kunde invalidates datasets that bind Kunde
- package-version changes invalidate all cached results naturally

The cache is scoped to the opened Blazor report instance and is not shared between users.

### Bounded parallel execution

Independent datasets requested for one page are executed concurrently with a per-report cap.

Default:

- `Runtime:MaxParallelDatasetsPerReport = 4`
- the existing process-wide `Runtime:MaxConcurrentQueries` remains the outer safety limit

This improves response time without creating an uncontrolled SQL query storm.

### Refresh semantics

- **Bericht ausführen** reuses still-valid dataset results and executes only stale datasets.
- **Aktualisieren** and Auto-Refresh force a fresh execution of the active page.
- page navigation uses cache-first lazy loading.

## Consequences

- parameter changes usually execute substantially fewer SQL statements
- expensive datasets are not recalculated for unrelated parameter changes
- detail pages no longer slow down overview refreshes
- page switching is fast after a page has already been loaded with the same parameter signature
- the query gate continues to protect the SQL Server from excessive concurrency
