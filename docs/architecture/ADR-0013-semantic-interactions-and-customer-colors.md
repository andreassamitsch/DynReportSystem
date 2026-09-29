# ADR-0013: Semantic report interactions and deterministic customer colors

- **Status:** Accepted
- **Date:** 2026-09-29
- **Applies to:** native DynReport charts, page navigation, report parameters and visual themes

## Context

Analytical cockpits must behave like one application rather than a set of isolated charts.

Users need to:

- use browser Back/Forward inside a multi-page report
- click a chart series to open its matching detail page
- click a customer bar or axis label to set the report customer filter and re-run all datasets
- keep the same customer visually recognizable across reports

These behaviors must remain renderer-neutral and designer-configurable.

## Decision

### Page history

Native report page changes are written to the browser History API.

The initial report page replaces the current history state. Each subsequent native page navigation pushes a new
history entry. Browser Back/Forward therefore moves through report pages before leaving the report route.

The active page is mirrored in the URL fragment as `#page=<id>`.

### Chart interactions

`DynChartDefinition.Interaction` defines semantic click behavior:

- `CategoryParameter`: clicked category/axis label sets this report parameter
- `SeriesParameter`: clicked series sets this report parameter
- `SeriesTargetPages`: maps a semantic series label to a native report page
- `RunReportAfterFilter`: re-runs the complete report after parameter mutation

`DynVisual.TargetPageId` remains the fallback detail-page target.

The chart renderer only emits a semantic point event. Parameter mutation and report execution remain runtime
responsibilities.

### Customer colors

The global color set `Customer` generates a stable color from the normalized customer name.

The algorithm:

- is deterministic across sessions and reports
- constrains saturation and lightness to chart-safe ranges
- darkens yellow/lime/cyan regions that would otherwise be weak on white dashboards
- avoids random/neon colors
- still allows explicit global or report-local overrides

This is intentionally distinct from the fixed `BusinessArea` semantic palette.

## Consequences

- a customer keeps the same visual identity across Top-Kunden, OP and Lager charts
- customer clicks can immediately become report-wide filters
- overview charts can route different series to different analytical pages
- browser navigation behaves naturally inside the report
- interaction semantics are portable in `.dynreport` and independent of ECharts
