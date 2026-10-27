# Kundencockpit native 1.5.0

Target runtime: **DynReport 0.18.0+**

## Performance

Kundencockpit now uses the page-scoped DynReport runtime.

- overview execution no longer preloads detail-page datasets
- opening Umsatz, Bestelleingang, Offene AB, Angebote, Offene Posten, Lager or Reklamationen loads only missing datasets for that page
- already loaded datasets are reused while their bound parameter values remain unchanged
- independent overview datasets run in bounded parallel batches
- server-mode detail tables remain paged and query only the currently visible page

### Lager KPI

The overview no longer loads the complete `LagerndeKundenartikel` result only to display one KPI.

A dedicated `LagerKPI` dataset reuses the proven analytical preparation but returns only:

- `LagerwertKritischGesamt`

The complete customer/article dataset runs only on the Lager page.

Because the Lager datasets bind only `Kunde` and `Vertriebsmitarbeiter`, a pure Von/Bis change does not invalidate their cached result.

## Dynamic detail pages

Customer-oriented charts continue to use the global `Customer` color set and global customer drill filtering.

Detail tables additionally map:

- `Kunde -> Kunde`
- `Vertriebsmitarbeiter -> Vertriebsmitarbeiter` where the field is present

Clicking one of these table cells updates the corresponding report parameter and re-evaluates the active report context.

## Refresh semantics

- `Bericht ausführen`: execute only stale active-page datasets
- page change: lazy-load only missing/stale page datasets
- `Aktualisieren`: force a fresh active-page execution

No connection strings or deployment-specific grants are stored in the package.


## Stacked total labels – runtime 0.19.2 fix

The `dashboard-main` visual in Kundencockpit 1.5.9 uses `Stacked=true`, `ShowStackTotal=true` and `StackTotalFormat=currency`. The options are correct; native DynReport prior to runtime 0.19.2 created an invisible ECharts line without a marker, which caused the total labels to be absent. The 0.19.2 renderer draws labels via tiny transparent point anchors and preserves the "primary-stack" responsive profile. The same runtime fix applies to other stacked charts with `ShowStackTotal=true`, including the Bestelleingang chart. Report data and package content stay unchanged.
