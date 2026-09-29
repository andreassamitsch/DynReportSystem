# Kundencockpit native 1.2.0

Target runtime: **DynReport 0.15.0+**

## Design goal

Version 1.2.0 brings back the proven SSRS cockpit principle:

- one dense desktop overview with all decisive sales indicators
- values shown where they materially improve scanning
- stable Geschäftsbereich colors
- direct navigation from KPI/chart to the matching analysis page
- detail tables and deeper analyses remain on separate pages

The native report does **not** copy SSRS rendering. It reproduces the information hierarchy with
renderer-neutral DynReport semantics.

## Overview

The overview uses one compact cross-dataset KPI strip with 11 metrics:

- Umsatz
- Bestelleingang
- Offene AB
- Rahmen offen
- Offene Posten
- Überfällige OP
- Reklamationskosten
- Offene Reklamationen
- Offene Angebote
- Angebote Hit-Rate
- Ø Angebots-Durchlaufzeit

KPI cards link directly to their detail area.

### Dashboard row 1

- Umsatz, Auftragsbestand und Rahmenplanung
  - Umsatz
  - offener Auftragsbestand
  - Rahmenplanung
  - total label above every stacked month
- Umsatzverteilung Top-Kunden
- Überfällige Posten > 10.000 €

### Dashboard row 2

- Umsatzentwicklung Geschäftsbereiche
- Offener Auftragsbestand inkl. Rahmen by Geschäftsbereich
- Hit Rate der Angebote by month
- Reklamationsentwicklung: count + cost

### Dashboard row 3

- Angebotsvolumen nach Status
- Angebotsdurchlaufzeit
- Bestelleingang nach Geschäftsbereich
- Top 10 Gründe verlorener Angebote

Compact charts are 180–220 px high so the desktop cockpit remains scan-friendly.

## Detail pages

The existing native detail pages remain:

- Umsatz & Länder
- Bestelleingang
- Offene AB
- Angebote
- Offene Posten
- Reklamationen

The Angebote page additionally receives Hit Rate, Durchlaufzeit and Ablehnungsgründe.
The Offene-AB page receives a combined AB/Rahmen potential analysis.

## Data logic

The package continues to use the modern native datasets for Umsatz, Bestelleingang, open AB and details.

Additionally, proven logic from the supplied Kundencockpit RDL is reused for:

- monthly Umsatz / open AB / Rahmen planning
- Angebotsstatus
- Angebotsdurchlaufzeit
- lost-offer reasons
- framework-order logic

`FU.AB_get` remains cross-database qualified as `[FUCHSHOFER].[FU].[AB_get]`.

## Colors

Charts use the global `BusinessArea` color set whenever a Geschäftsbereich is encoded.

The global color map remains the single source of truth. Local report overrides are only used for
non-business-area semantics such as offer status or rejection type.

## Native SQL boundary

Some mature SSRS queries start with local `DECLARE` statements. Runtime 0.15 allows these read-only
preambles and `;WITH` CTE syntax.

The write boundary stays unchanged: DML/DDL, EXEC and SELECT INTO remain prohibited.

## Lagerwert kritisch

The legacy LagerndeKundenartikel dataset intentionally stays out of 1.2.0 for now. Its RDL query relies on
temporary-table DDL (`SELECT INTO`, `CREATE INDEX`). Allowing that in arbitrary report packages would
weaken the native read-only SQL boundary.

The correct next step is a dedicated read-only FU view/function/stored procedure for this calculation.
Once available, the KPI and Lager detail page can be added without changing the cockpit architecture.
