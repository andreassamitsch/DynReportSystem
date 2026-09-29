# Kundencockpit native 1.4.0

Target runtime: **DynReport 0.17.0+**

## Interaction changes

The overview and customer analyses are now interactive:

- clicking **Umsatz** in the main Umsatz/Auftragsbestand/Rahmen chart opens the Umsatz page
- clicking **Offener Auftragsbestand** or **Rahmenplanung** opens the Offene-AB page
- clicking a customer bar or customer axis label sets the report parameter `Kunde`
- after a customer click, the complete Kundencockpit is re-run for that customer
- browser Back/Forward navigates through report pages before leaving the report

The current report page is reflected in the URL fragment.

## Stable customer colors

Customer-oriented charts use `ColorSet=Customer` and `ColorKeyField=Kunde`.

The color is generated deterministically from the normalized customer name by the DynReport runtime. The same
customer therefore keeps the same color in:

- overview Top-Kunden
- overdue customer OP
- Umsatz Top-Kunden
- Lager critical-value-by-customer

The generated colors are constrained to visualization-safe saturation/lightness ranges. Global or report-local
overrides remain possible.

## Readability fixes

- all 12 overview KPIs fit into one row on typical widescreen desktop layouts
- KPI labels and chart subtitles use larger, higher-contrast text
- compact stacked charts no longer force angled date labels at narrow widths
- stack totals use a dedicated invisible helper series, so the total also renders for months where only one stacked
  component has a value

## Permissions and data

No connection strings or deployment-specific ACL grants are embedded in the package.
