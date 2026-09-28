# Kundencockpit modernization baseline — DynReport 0.12

## Runtime rule

The report id remains `kundencockpit`.

When a native `.dynreport` package with this id is installed, the portal and the historic
`/reports/kundencockpit` URL both use the generic package runtime. The hand-written
Razor/RDL cockpit remains only as a transition fallback when no native package exists.

No Kundencockpit-specific rendering behavior is allowed in the native runtime.

## Heavy-data strategy

The native Kundencockpit should use:

- charts/KPIs: aggregated datasets required by the visual
- large detail tables: `DataMode=Auto` or `Server`
- grouped analysis: `PresentationMode=Grouped`
- cross-tab analysis: `PresentationMode=Pivot`
- SQL Server for filtering, sorting, grouping and aggregation
- browser/renderers only for the bounded result page or bounded aggregate matrix

`DataMode=Auto` now selects server execution automatically when it is safe.

## Recommended analytical views

### Umsatz

Keep the existing time/GB charts as chart datasets.

For the large Umsatz detail table:
- server detail rows where the SQL is composable
- optional grouped analysis by `Geschäftsbereich`, `Kunde`, article/customer dimensions as available
- measure: `Sum(Betrag)`

Useful pivot:
- row dimensions: customer and/or business area
- column dimension: month/year field from the dataset/read model
- measure: revenue amount

### Bestelleingang

The established business logic for position/head surcharges and the current AB/VA behavior
must remain unchanged.

Recommended analysis:
- grouped by business area
- measure: `Sum(BestellwertInklZuAbschlag)`
- optional month × business-area pivot

### Offene AB / Rahmen

Keep business logic for open order and framework values in SQL/read models.

Large detail output should be server-paged. Grouped totals may be produced server-side rather
than by loading every open position into the browser.

### Angebote

Use server detail rows for the large offer list.

Status, rejection reason and lead-time visuals remain summary/analytical datasets. Optional
grouped tables can aggregate offer value by status/business area.

### OP / Reklamationen / lagernde Kundenartikel

These are detail-oriented datasets and are strong candidates for table-only `DataMode=Auto`.
If their SQL is composable, DynReport will automatically use the server query path.

## SQL migration constraint

The Library RDL available to the project is dated July 2026 and predates later Kundencockpit
changes such as the September Bestelleingang/GB work. It must **not** be used as an
authoritative replacement for the current APP-01 report logic.

Some RDL datasets also contain `DECLARE`, CTE batches or terminal `ORDER BY`. Those queries
must not be rewritten mechanically by DynReport.

For server-mode native reports, move such logic behind a stable database read model
(view / table-valued function / dedicated FU query object where appropriate), then expose a
single composable SELECT to the `.dynreport` dataset.

## Transition fallback improvements

If no native package exists, the legacy Razor Kundencockpit remains available but:
- it is explicitly marked as legacy fallback
- overview datasets execute with bounded parallelism instead of strictly sequential execution
- the configured default is four concurrent dashboard dataset queries

The legacy path should receive only critical fixes. New report functionality belongs in the
native package/designer/runtime.
