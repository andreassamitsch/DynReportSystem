# DynReport System

Internal reporting and analytics platform for APP-01. The goal is a modern successor path for selected SSRS reports while retaining Windows SSO, folder/report permissions and existing RDL data/business logic.

## Version 0.2.2

- ASP.NET Core / Blazor Interactive Server on .NET 10
- IIS Windows Authentication (SSO)
- explicit folder/report ACLs using Windows users / AD groups
- runtime parsing of RDL dataset SQL and supported report parameters
- automatic reload when the installed RDL changes
- independent DynReport dashboard definitions instead of reproducing SSRS layouts
- Apache ECharts 6.1.0 bundled locally; no chart CDN is required at runtime
- compact KPI cards with trend and sparklines; the Umsatz/AB/Rahmen chart is the primary dashboard element
- stacked Umsatz / offene AB / Rahmen monthly chart using the established RDL colors, with a visible monthly total above every stack
- persistent collapsible parameter/filter bar that stays available while scrolling
- stacked business-area charts whose GB colors are read dynamically from the installed RDL
- mobile-specific ECharts layouts for rankings, stacked charts, donut charts and long labels
- interactive combo, line, area, bar, donut, stacked and treemap visualizations
- world map for revenue by country
- dataset-specific analytics such as OP aging, complaint trend, stock value/status and business-area analysis
- interactive table search, sorting and a filter input on every column, paging and CSV export
- chart-to-table drilldowns and internal back-navigation between dashboard and detail views
- longer mobile circuit retention and more persistent reconnect attempts for brief WLAN/tab interruptions
- chart image export
- PWA shell without caching report data
- self-contained web application payload; IIS ASP.NET Core Module v2 remains a server prerequisite

## Architecture

DynReport System does **not** reproduce the SSRS page layout.

- **RDL / SQL layer:** existing datasets, parameters and business logic can be reused during migration.
- **DynReport definition:** modern layout, widget bindings, chart types, aggregation and interaction rules.
- **Blazor runtime:** renders responsive reports and enforces Windows/AD permissions.
- **ECharts runtime:** rich local visualization without requiring Internet access on APP-01.
- **Future designer:** will edit DynReport definitions visually rather than the old RDL page layout.

The Kundencockpit is the first migrated report. The RDL remains the data source, while the user-facing report is an independent interactive dashboard.

## Kundencockpit 0.2.2

The dashboard includes:

- Umsatz / offene AB / Rahmen as the large primary stacked monthly chart
- compact Umsatz, offene AB and Rahmen KPIs
- Umsatz and Bestelleingang as stacked business-area series using RDL GB colors; Umsatz now resolves the actual SQL field `Geschäftsbereich Nr` correctly
- top customers as an interactive, mobile-optimized horizontal ranking
- business-area drilldown by tapping a stack segment
- offer-status donut
- rejection-reason ranking
- offer lead-time trend
- revenue world map and business-area treemap in the revenue detail
- open-items aging
- complaint count/cost trend
- stock status and customer-bound inventory value charts
- modern searchable/sortable detail tables with CSV export

## Security

Production RDLs are not committed while this repository is public. They can contain internal database schema and business logic. The included setup copies RDL files located next to the installer into `C:\Program Files\DynReportSystem\Reports`.

SQL credentials belong only in `appsettings.Production.json` on APP-01. Never commit them.

The installer preserves an existing production configuration, report permissions and IIS site bindings during an update.

## Build

GitHub Actions builds `DynReportSystem-Server-Setup-0.2.2-win-x64.exe`.

The workflow installs the pinned frontend dependencies, vendors ECharts and the world SVG map into the published application, publishes the self-contained .NET application and creates the server setup executable.

## Third-party components

- Apache ECharts 6.1.0 — Apache-2.0
- @svg-maps/world 2.0.0 — CC BY 4.0

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## License

DynReport System itself is proprietary / all rights reserved. See [LICENSE](LICENSE).
