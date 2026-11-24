# Phase 1 notes: page code (code reading, before the baseline)

Read from the code on 2026-11-23 (release-3.1.4 = v3.1.3). Not measured yet: the baseline decides what is fixed.
Paths are under `dashboard\CopilotInsights\Pages\`. At 730 days and All data, `DateKey BETWEEN @k0 AND @k1` covers
nearly the whole table.

## Pages that only call Data\ (see phase1-shared-queries.md)

/leadership, /leadership/org, /leadership/act, /leadership/value, /leadership/costs, /leadership/reservations,
/leadership/credit-chargeback, /leadership/assumptions, /leadership/boardpack, /licensing/pool, /licensing/chargeback,
/licensing/leavers, /licensing/suites, /licensing/credit-use, /adoption/targets, /forecasting/*, /workforce (four
ByAttribute calls in a row), /leadership/compare (Totals and Trend per side, then one FactInteraction pass per app
per side).

## Fixed windows (do not grow with the period)

/licensing/fading, /licensing/swap, /forecasting/actions, /forecasting/waves, /adoption/retention,
/adoption/cohorts, /adoption/not-started, /licensing/unused, /licensing/overlap, /licensing/departures,
/licensing/non-person, /licensing/reservation-holders, /adoption/anomalies. Outliers: Actions.cs:153-155 reads
FactInteraction with no date bound (fine for 50 people, heavy for the CSV); Licensing.cs:172 an unbounded
`OUTER APPLY MAX(DateKey)` on UserDay per licensed person.

## Likeliest over 1 s, by cause

**A. Agent by person, or distinct people per agent, from FactInteraction; "(no agent)" dropped only after grouping**
- /agents (Agents.cs:31-41, then 42-48 and 55-57 in a row)
- /agents/inventory (Workbook.cs:166-170; OperationsReports.cs:335-339 without a workbook)
- /agents/owners (Workbook.cs:287-291), /agents/checks (Workbook.cs:387-391), /agents/children (Workbook.cs:496-500)
- /leadership/mix (Leadership.cs:131-139), /agents/credits (Credits.cs:41-47)
- /agents/detail (Agents.cs:182-183: no lower date bound; five more passes 174-235)

**B. Person (or person by day) grouping inside FactInteraction**
- /apps (Apps.cs:31-47), /apps/detail (Apps.cs:128-157, five passes plus FactAccessedResource)
- /enablement (Enablement.cs:84-91), /governance (Governance.cs:116-134, plus 18-40)
- /leadership/usecases (BoardPack.cs:206-216), /licensing/demand (Licensing.cs:259-265; UserDay CTE unscoped, twice)

**C. FactAccessedResource (about 280M rows)**
- /compliance/sites (OperationsReports.cs:217-226), /compliance (Compliance.cs:48-62)
- /compliance/units (MoreReports.cs:85-96), /adoption/by-license (ByLicense.cs:130-188, FactSession too)

**D. Per-person ranking on ResourceDay**
- /compliance/access (Compliance.cs:164-213)

**E. Every person's UserDay rolled up with no scope, the scope applied after**
- /enablement/people (Enablement.cs:30-32, 183), /enablement/champions (OperationsReports.cs:473-489)
- /licensing/ranking (Ranking.cs:66-87), /licensing/payg (PayAsYouGo.cs:70-91)
- /workforce/managers (Workforce.cs:71), /workforce/families (PeopleReports.cs:255-268)
- /adoption/departments (Adoption.cs:203-216), /security/guests (PeopleReports.cs:84-86)
- /adoption (Adoption.cs:43-46: first day per person over all of UserDay)

**F. Distinct people per bucket from UserDay**
- /regions (Regions.cs:31-37), /security (Security.cs:19-33)

**G. Several full-period scans in a row, counts only**
- /regions/patterns (Regions.cs:59-95), /enablement/depth (MoreReports.cs:150-162), /agents/handoffs
  (MoreReports.cs:118-127), /compliance/person (PeopleReports.cs:218)

**In C#:** no page loads everyone and groups in memory. Sparklines rebuilt with `.Where(...).Sum` per bucket
(Agents.cs:80, Apps.cs:76, Compliance.cs:78) grow with rows times buckets: about 104 weeks at 730 days.
