# ADR-0001: Self-contained, designer-first `.dynreport` packages

- **Status:** Accepted
- **Date:** 2026-09-26
- **Decision owner:** Fuchshofer / DynReport System
- **Supersedes:** the 0.4 automatic RDL-presentation approach and the 0.5 report-specific renderer approach

## Context

The SSRS migration proved that an automatic transformation of RDL layout into a modern web report is useful as a migration aid, but not sufficient for production-quality reports. The RDL contains valuable business intent, calculations and presentation choices, yet a good DynReport version must be analysed and redesigned report by report.

The first 0.5 prototype for `Produktion Übersicht TV` moved in that direction, but still referenced the migrated RDL for SQL/parameters and used a report-specific Razor renderer. That would create a new hard-coded application surface for every report and would make a future visual designer impossible.

## Decision

A finished DynReport report is a **single self-contained `.dynreport` package**. The package is the only runtime definition of the report.

The original RDL is allowed only as a **migration/analysis input**. Once a report has been converted, the runtime must not require the RDL, RSD, RDS or ReportServer database.

A `.dynreport` package contains:

- report identity, title, folder/path, version and description
- report access rules
- data-source profile references and non-secret connection metadata
- complete dataset definitions, including SQL/stored-procedure command and parameter mappings
- report parameters, defaults and value lists
- calculated fields / aggregate expressions
- layout tree
- visual components
- conditional formatting and status mappings
- interactions and refresh settings
- responsive layout information
- optional assets

Credentials and secrets are **never embedded**. A report references a centrally configured data-source profile such as `SyncosPRD` or `OxaionPRD`.

## Designer-first rule

The runtime and the future designer use exactly the same document model.

**No report feature may be added in report-specific C#, Razor or JavaScript if that feature cannot also be represented in the `.dynreport` schema and edited by the designer.**

Therefore:

- no `ProductionTvOverview.razor`-style per-report renderer
- no per-report calculation service
- no hard-coded report IDs in runtime rendering
- reusable generic components only
- expressions/conditions stored declaratively
- layout stored declaratively
- imports and designer saves create package revisions
- the same package can be imported, exported, edited and executed

## Package format

`.dynreport` is a ZIP container with a stable public schema.

Initial 2.0 structure:

```text
<report>.dynreport
├── manifest.json
├── report.json
└── datasets/
    └── <dataset>.sql
```

Future versions may add:

```text
assets/
themes/
locales/
```

without changing the one-file deployment model.

## Runtime model

The runtime renders generic component types such as:

- heading/text
- KPI / metric strip
- grouped board
- table / matrix
- chart
- status/badge
- container / grid / repeat

Values and conditional formatting are driven by the generic expression model in the package.

## Designer model

The future visual designer edits the same structures:

- Data
- Parameters
- Calculations
- Layout
- Components
- Conditional formatting
- Interactions
- Responsive rules

A designer save must produce a valid `.dynreport` package that can be exported and imported without a rebuild of DynReport System.

## Versioning

The package has its own report version. Installing or saving a replacement creates a revision before the current package is overwritten. Previous revisions can later be compared/restored by the designer UI.

## Security

- package import requires Publish or Manage permission
- data-source secrets stay in server configuration
- SQL is executed only server-side
- package ZIP paths are never extracted blindly
- package size and required entries are validated
- the renderer does not execute arbitrary C#, VB, JavaScript or Razor stored in the package

## Consequences

### Positive

- RDL is no longer a runtime dependency for converted reports
- every converted report is portable as one file
- report versions can be delivered independently of application releases
- designer-created and assistant-created reports are equivalent
- 260 reports do not require 260 Razor components
- reports can survive a later complete removal of SSRS

### Cost

- RDL migration becomes a deliberate report-by-report redesign
- the generic component/expression library must grow when a genuinely new visual or calculation pattern is required
- unusual SSRS features are migrated explicitly rather than emulated automatically

## Migration policy

For each SSRS report:

1. analyse business purpose, query, parameters, groupings, expressions, visibility and formatting
2. decide the appropriate DynReport UX
3. copy all required data/query logic into the package
4. reproduce business calculations with declarative DynReport expressions
5. validate results against SSRS
6. import the package
7. only then consider the original RDL obsolete
