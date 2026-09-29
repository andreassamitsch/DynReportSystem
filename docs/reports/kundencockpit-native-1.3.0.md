# Kundencockpit native 1.3.0

Target runtime: **DynReport 0.16.0+**

## Fixes from 1.2.0

The three offer dashboard datasets that could return no rows with an empty customer filter were corrected to treat
an empty / NULL `@Kunde` as “all customers”:

- AngebotsStatusAnalyse
- AngebotsDurchlaufzeit
- AngebotsAblehnungsgruende

This matches the intended SSRS filter semantics while keeping the existing salesperson/business-area assignment.

## Overview refinements

- 12 compact KPIs including restored `Lagerwert kritisch`
- Umsatz/Auftragsbestand/Rahmen remains the dominant chart
- Top customers and large overdue receivables use compact horizontal ranking bars with directly readable values
- Umsatzentwicklung Geschäftsbereiche is again a stacked area view
- monetary value axes use compact German notation
- monthly stack totals are thinned automatically when the chart becomes too narrow
- all overview analytics retain direct page jumps

## Lager & Kundenartikel

The proven RDL calculation is restored as native dataset `LagerndeKundenartikel`.

The query uses only SQL Server local `#temp` tables and temp indexes. Runtime 0.16 allows these while still rejecting
persistent DDL/DML.

The Lager page contains:

- total allocated customer-stock value
- prognostically free stock value
- critical stock value
- customer-article count
- critical stock value by customer
- stock value distribution by status
- searchable/filterable customer-article detail table

The existing business logic for customer/article allocation, open orders, framework orders, forecasts and future
demand is retained from the supplied RDL.

## Security

The package contains no environment-specific grants and no connection strings.
Portal permissions and centrally configured data sources remain authoritative.
