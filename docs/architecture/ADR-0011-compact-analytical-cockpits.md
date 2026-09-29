# ADR-0011: Compact analytical cockpit density

- **Status:** Accepted
- **Date:** 2026-09-29
- **Applies to:** native DynReport runtime and Designer

## Context

Analytical cockpit reports must support a different information density from operational detail reports.
The existing Kundencockpit proved that a desktop user benefits from seeing the decisive KPIs and trend
signals on one monitor, while details remain one click away.

The runtime previously treated every metric and chart as a relatively large standalone visual. That was
appropriate for general reports but wasted too much vertical space for management cockpits.

## Decision

DynReport adds a semantic report density setting:

- `Comfortable` remains the general default.
- `Compact` optimizes desktop analytical cockpits.

Compact density is a presentation policy, not report-specific CSS. The runtime reduces report chrome,
parameter spacing, page navigation, KPI card height and dashboard gaps while preserving mobile
responsiveness.

A metric strip may contain metrics sourced from different datasets. Every metric may define its own
`Dataset` and optional `TargetPageId`.

Visuals may define `TargetPageId` so overview charts act as navigation into their analytical detail page.

Charts add renderer-neutral semantics required by dense cockpits:

- fixed series color
- per-series chart type
- primary/secondary value axis
- optional filled area
- horizontal orientation
- formatted visible value labels
- total label above stacked bars
- category colors resolved from global color sets / local overrides

The Designer exposes all of these semantic options.

## Security

Supporting migrated SSRS queries does not relax the database write boundary.

Text datasets may begin with local `DECLARE` statements or a leading semicolon before a CTE.
Write-capable SQL, `SELECT ... INTO`, `CREATE`, `EXEC` and the existing forbidden operations remain
blocked.

## Consequences

- Cockpit overview pages can closely match the information density of mature SSRS dashboards.
- Overview and detail pages remain one self-contained `.dynreport` package.
- KPI values can reuse purpose-built summary datasets without forcing detail datasets into client memory.
- Global business-area colors remain consistent.
- Complex legacy datasets that depend on temp-table DDL still require a dedicated read-only view/function/
  stored procedure before they can become native datasets.
