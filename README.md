# DynReport System

Internal reporting platform for APP-01. The goal is a modern successor path for selected SSRS reports while retaining Windows SSO, folder/report permissions and existing RDL SQL logic.

## Pilot 0.1.0

- ASP.NET Core / Blazor Interactive Server on .NET 10 LTS
- IIS Windows Authentication (SSO)
- explicit folder/report ACLs using Windows users / AD groups
- runtime parsing of RDL dataset SQL and supported report parameters
- automatic reload when the installed RDL changes
- Kundencockpit as the first modernized report
- PWA shell without caching report data
- self-contained web application payload; IIS ASP.NET Core Module v2 remains a server prerequisite

`DynReport System` does **not** try to reproduce the SSRS page layout. The RDL is used as a migration/data-logic source for SQL datasets, parameters and field definitions. The modern report layout is stored separately as a DynReport dashboard definition. Widgets (KPIs, charts, rankings and drill-down links) are responsive and interactive. The planned visual browser designer will edit this DynReport definition rather than the old RDL page layout.

## Security

Production RDLs are not committed while this repository is public. They can contain internal database schema and business logic. The included setup copies RDL files located next to the installer into `C:\Program Files\DynReportSystem\Reports`.

SQL credentials belong only in `appsettings.Production.json` on APP-01. Never commit them.

## Build

GitHub Actions builds `DynReportSystem-Server-Setup-0.1.0-win-x64.exe`. The setup payload is self-contained for .NET, but IIS must have ASP.NET Core Module v2 installed. Windows Authentication is enabled for the IIS site by the installer.

## License

Proprietary / all rights reserved. See [LICENSE](LICENSE).


## Architecture direction

- **RDL / SQL layer:** existing business logic can be reused during migration.
- **DynReport definition:** independent modern layout, widget bindings, interaction rules and responsive sizing.
- **Blazor runtime:** renders the dashboard and enforces Windows/AD permissions.
- **Future designer:** edits DynReport definitions visually; no dependency on the SSRS layout designer.

The Kundencockpit pilot already supports interactive month filtering, customer drill/filter actions and dynamic detail datasets.
