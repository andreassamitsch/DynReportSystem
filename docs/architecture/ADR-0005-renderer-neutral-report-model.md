# ADR-0005: Renderer-neutral report model

- **Status:** Accepted
- **Date:** 2026-09-28
- **Decision owner:** Fuchshofer / DynReport System
- **Applies to:** native `.dynreport` schema, runtime, designer and future visual components

## Context

DynReport must remain maintainable even if the currently used UI, chart, grid, map or visualization libraries are later replaced, extended or used side by side.

A report definition is a long-lived business artifact. A rendering framework is an implementation detail with a shorter lifecycle. Coupling report packages to a specific JavaScript, Blazor, chart or data-grid library would make future framework changes require migration of every stored report.

## Decision

The `.dynreport` format is **renderer- and framework-neutral**.

A native report describes **what** should be shown and how users may interact with it in semantic DynReport concepts. It must not describe **how a particular third-party framework implements it**.

Framework-specific configuration is confined to renderer adapters inside the DynReport runtime.

### Binding rule

The following direction is mandatory:

```text
.dynreport
    |
    v
semantic DynReport document model
    |
    +-- data/query model
    +-- expression model
    +-- layout model
    +-- interaction model
    +-- visual component model
    |
    v
renderer abstraction / capability layer
    |
    +-- chart renderer adapter
    +-- table/grid renderer adapter
    +-- map renderer adapter
    +-- other visual adapters
    |
    v
concrete framework(s)
```

A concrete framework may be replaced without changing existing report definitions as long as the replacement renderer supports the required semantic capabilities.

## Prohibited package coupling

Native report packages must not contain framework-specific configuration contracts such as:

- ECharts option objects or ECharts-specific series configuration
- AG Grid column definitions or grid API settings
- Plotly traces/layout objects
- Telerik, Syncfusion, DevExpress or MudBlazor component settings
- raw Razor component names
- framework-specific JavaScript callbacks
- framework-specific CSS selectors required for business behavior

Generic CSS class hooks may exist for presentation/theming, but report behavior must not depend on a concrete renderer's internal DOM structure.

## Semantic component model

### Charts

Charts describe concepts such as:

- chart family/type
- dataset
- category/dimension fields
- value/measure fields
- aggregation
- series/grouping
- stacking
- sorting
- labels
- legend
- tooltip content
- axes and scales
- conditional formatting
- drilldown/actions
- responsive behavior

The chart renderer translates these properties into the concrete library configuration.

### Tables and grids

Tables describe concepts such as:

- visible columns and order
- data types and formatting
- default sorting
- user sorting capability
- filters and filter operators
- grouping hierarchy
- aggregates/subtotals
- paging/virtualization intent
- conditional formatting
- row/column actions
- selection behavior
- drilldown
- responsive behavior

The report must not depend on a particular grid implementation.

Small tables, large virtualized datasets, grouped analysis grids and server-paged result sets may use different runtime renderers while consuming the same semantic report definition.

### Other components

The same rule applies to maps, KPI components, grouped boards, gauges, timelines, trees, pivots and future visual types.

## Filtering, sorting and grouping

Filtering, sorting and grouping are first-class DynReport concepts and must not be stored as renderer configuration.

Where execution happens is a runtime decision:

- client-side for small datasets
- server-side for large datasets
- pushed into SQL where appropriate and safe
- hybrid when useful

The report expresses the requested behavior; the runtime chooses the execution strategy.

## Renderer capabilities

Renderer implementations expose capabilities to the runtime rather than leaking framework identities into the report.

Examples:

- `chart.stacked`
- `chart.multiaxis`
- `table.sort`
- `table.filter`
- `table.group`
- `table.virtualize`
- `table.pivot`
- `interaction.drilldown`
- `interaction.selection`

If the preferred renderer does not support a required capability, the runtime may select another compatible renderer or present a controlled unsupported-feature message. It must not silently reinterpret report semantics.

## Designer rule

The visual designer edits only the semantic DynReport model.

Designer controls may expose choices such as:

- "gestapelte Säulen"
- "nach Geschäftsbereich gruppieren"
- "Spalte filterbar"
- "absteigend sortieren"
- "Zwischensumme anzeigen"

They must not expose implementation concepts such as ECharts `series` objects or AG Grid `columnDefs`.

## Schema rule

The JSON Schema is the stable contract and must be progressively tightened around semantic component definitions.

New component features require:

1. a framework-neutral schema representation
2. runtime semantics
3. renderer capability/adapter implementation
4. designer support
5. migration/default behavior for older package revisions where required

A new feature must not be added only as an opaque vendor-specific JSON bag.

## Multiple renderers

DynReport may use several frameworks at the same time.

For example:

```text
chart          -> chart renderer A
standard table -> lightweight DynReport table renderer
large grid     -> grid renderer B
map            -> map renderer C
```

This is an internal runtime choice. The report package remains portable and unchanged.

## Consequences

### Positive

- existing reports survive framework replacements
- different renderers can coexist
- charts and grids can evolve independently
- specialized high-performance grids can be introduced later
- framework licensing or maintenance changes do not force report rewrites
- designer/runtime remain based on one stable business-oriented model

### Cost

- DynReport needs an explicit semantic schema and renderer abstraction
- new third-party framework features cannot be exposed directly without first modeling their generic meaning
- adapters may not expose every vendor-specific feature
- capability fallback and compatibility testing become runtime responsibilities

## Enforcement

Code review and future implementation work must treat any framework-specific property entering a `.dynreport` document as an architecture violation unless it has first been modeled as a generic DynReport semantic concept.

This ADR is a binding rule for all further native report development.
