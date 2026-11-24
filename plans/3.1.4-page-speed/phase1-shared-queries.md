# Phase 1 notes: the shared query layer (code reading, before the baseline)

Read from the code on 2026-11-23 (release-3.1.4 = v3.1.3). Not measured yet: the baseline decides what is fixed.
Paths are under `dashboard\CopilotInsights\`.

## How every report query is scoped and hinted

- Org scope: `ReportContext.Scope()` (ReportContext.cs:272) joins `dash.fnScopeUsers(@root)` (OrgClosure to DimUser);
  `PersonScope()` (:290) joins OrgClosure. The whole organization adds nothing.
- Enabled today: `EnabledJoin` (:283) joins `bi.DimUser ... AccountEnabled = 1`; `IsEnabled` (:287) tests it.
- `PlanHints.Apply` (PlanHints.cs:47) on every read: scoped statements copy the scope into `#scope` and get
  `OPTION (RECOMPILE)`; the whole organization gets `OPTIMIZE FOR UNKNOWN`; a statement naming no date key, nothing.
- `PageWarmer` builds the whole organization only, every period: any `?org=` view is always a first visit.
- All data's first day: `SELECT MIN(DateKey) FROM dash.UserDay` (ReportContext.cs:378), a seek on `PK_DashUserDay`.

## Likeliest over 1 s at 730 days and All data

1. `CreditData.BuildFreshAsync` (CreditData.cs:63-85): `bi.FactInteraction` grouped by person and agent over the
   period, then twice more; loads all 210k DimUser rows. /agents/credits, /licensing/credit-use,
   /licensing/license-for-credits.
2. `OpsReports.ValueAsync` (OpsReports.cs:182, :197): two scans of FactInteraction over the period; the second a
   distinct count of people by department. /leadership/value, board pack, and through CostData /leadership/costs and
   /leadership/act.
3. `Metrics.AdoptedAsync` (Metrics.cs:124-138): a LEAD over every licensed person-day times 28. /leadership.
4. `Metrics.TrendAsync` (Metrics.cs:74-105): distinct people per bucket over UserDay, read twice; FactLicensedDay at
   each bucket end. Ten page files.
5. `Metrics.UnitsTrendAsync` (Metrics.cs:246-253): distinct people per unit and bucket. /leadership/org.
6. `CostData.BuildFreshAsync` (CostData.cs:151-157): UserDay joined to FactLicensedDay over the period, unscoped, then
   about 25 more queries in a row. /leadership/costs, /leadership/act.
7. `Metrics.TotalsAsync` (Metrics.cs:143-171): `EverActive` (:161) is a distinct over all of UserDay with no date
   bound, at every period; `TargetsAsync` calls Totals twice per target.
8. `Metrics.UnitsAsync` (:198) and `ByAttributeAsync` (:285): the whole period's UserDay aggregated with no scope, the
   scope applied after; Workforce calls ByAttribute four times, Regions twice.
9. `Metrics.UnitSeriesAsync` (:260): unit by bucket sums.
10. `OpsReports.LicenseMonthsAsync` (OpsReports.cs:228): `ld.DateKey / 100 IN @months` scans all of FactLicensedDay.
11. `ReportContext` children list (ReportContext.cs:457-460): a correlated enabled count per direct report; 0.4 s.

Fact readers in the shared layer: CreditData.cs:69, OpsReports.cs:184 and :199 (pages); Delivery.cs:90 and :97
(scheduled extracts, not pages). None read FactAccessedResource or FactSession.
