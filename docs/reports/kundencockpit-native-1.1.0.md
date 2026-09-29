# Kundencockpit native 1.1.0

Target runtime: **DynReport 0.14.0+**

## Pages

1. Übersicht
   - Umsatz KPI
   - Bestelleingang KPI
   - offene AB KPI
   - Angebotswert KPI
   - Umsatz nach Geschäftsbereich / Monat
   - Top-Kunden
   - Bestelleingang nach Geschäftsbereich / Monat
   - Angebotsstatus

2. Umsatz & Länder
   - Geschäftsbereichsverlauf
   - Geschäftsbereich-Treemap
   - Weltkarte Umsatz nach Land
   - Tooltip der Weltkarte zeigt den umsatzstärksten Kunden der Länderaggregation
   - Top-Kunden
   - serverseitig paginierte Umsatzbuchungen

3. Bestelleingang
   - Monatsentwicklung nach Geschäftsbereich
   - Geschäftsbereich-Treemap
   - Kunde × Monat Pivot
   - serverseitig paginierte Details

4. Offene AB
   - offener Auftragswert
   - Geschäftsbereich-Treemap
   - serverseitige GB-Aggregation
   - serverseitig paginierte Details

5. Angebote
   - Angebotswert
   - Status-Donut
   - Geschäftsbereich-Treemap
   - serverseitig paginierte Details

6. Offene Posten
   - offener Betrag
   - OP-Aging
   - serverseitig paginierte Details

7. Reklamationen
   - Reklamationskosten
   - Monatsverlauf Reklamationen
   - serverseitig paginierte Details

## Data-source rules

- Oxaion data uses native source `OxaionPRD` with target catalog `production`.
- Bestelleingang and offene AB use the cross-database TVF:
  `[FUCHSHOFER].[FU].[AB_get](...)`
- Syncos complaint data is read cross-database from:
  `[syncos_prd_102].[ITSDEV]`
- internal customer `2000956 / 000` remains excluded from sales/order datasets.
- sales responsibility uses Geschäftsbereich assignment `VKGBSP` first and customer-master `VKUNDP` as fallback.

## Geschäftsbereich colors

Charts use `ColorSet=BusinessArea`.

The global palette lives in server configuration and reproduces the original RDL code.
A report can override individual keys with `ColorOverrides`, but should normally inherit the global mapping.

## Performance

Detail tables use `DataMode=Auto`. Because their SQL is composable and they are table-only consumers, the runtime selects server-side paging/filtering/sorting automatically.

Summary datasets remain deliberately aggregated for charts/KPIs.

## Permissions

The package contains no deployment-specific ACL grants. Portal/folder permissions remain authoritative and are not replaced by report imports.
