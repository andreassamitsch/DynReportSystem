# ADR-0010: Global visual themes and native multi-page reports

- **Status:** Accepted
- **Date:** 2026-09-29
- **Decision owner:** Fuchshofer / DynReport System
- **Applies to:** native DynReport charts, designer, report pages, legacy cockpit charts

## Context

Business-area colors are business semantics, not report-specific decoration. The same Geschäftsbereich must therefore use the same color in every report.

The former Kundencockpit already defined a stable color mapping in its RDL code. Native reports must preserve that visual language without keeping an RDL dependency.

Native reports also need real page navigation instead of rendering every page below the previous one.

## Decision

DynReport introduces global named color sets under `VisualTheme:ColorSets`.

The built-in `BusinessArea` color set reproduces the established RDL mapping:

- 00 / 03 / Allgemein: `#b5b5b5`
- 10 / CNC: `#90ceff`
- 11 / US: `#cebbe9`
- 12 / Zerodur: `#efe68c`
- 13 / Keramik: `#6aaa60`
- 21 / FAM: `#7ae5da`
- 22 / FAM+: `#83fcb9`
- 31 / HEPHAX: `#ffc65c`
- 999: `#222222`
- 102: `#3398FF`
- 103: `#00FFFF`
- 104: `#FF9833`

A chart selects a global set through `ColorSet`. `ColorKeyField` may differ from the displayed category/series field, allowing e.g. display text `FAM` while resolving the color by GB code `21`.

A report may override individual keys through `ColorOverrides`. Overrides are local to that chart and do not alter the global theme.

## New native chart capabilities

Renderer-neutral chart semantics now include:

- `WorldMap`
- `Treemap`
- named global color sets
- separate color key field
- local color overrides
- semantic tooltip metadata fields

The ECharts adapter remains an implementation detail.

## Native page navigation

When a `.dynreport` contains multiple pages, the runtime renders a page navigation bar and only the active page body.

This reduces visual noise and restores the intended cockpit workflow:

- Übersicht
- Umsatz
- Bestelleingang
- Offene AB
- Angebote
- additional analytical pages

The page model itself is unchanged; navigation is runtime behavior.

## Compatibility

Legacy Kundencockpit charts consume the same global BusinessArea color set through the compatibility style service. This prevents the legacy and native paths from diverging visually during migration.

## Consequences

### Positive

- Geschäftsbereich colors are consistent system-wide
- one central change can update every report
- report authors no longer duplicate corporate/business semantics
- individual reports can still override exceptional colors
- maps and treemaps are first-class native visuals
- multi-page reports behave as actual reports rather than one long document

### Constraints

- global theme changes are deployment-wide and therefore belong in server configuration
- package overrides should be used sparingly because they intentionally break global consistency
