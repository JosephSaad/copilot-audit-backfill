# 3.1.4 plan: every page under 1 second at 120 million interactions

Status: draft, 2026-11-23. Nothing built yet. Based on `main` at 9ef0578 (3.1.3).

## The goal

Every page opens in under 1 second, measured at the server, **on its first visit**, when nothing is kept for it
yet. That covers:

- every page in the menu, Insights and Workbench;
- every period: 7, 14, 30, 90, 180, 365 and 730 days, and All data;
- three scopes: the whole organization, a large part (about 70,000 people) and a team (about 600 people);
- pages where you pick something (a person, an agent, a site), tested with one typical pick each.

Pages served from the cache already take 0.01 to 0.06 s and must stay that fast. So the real work is first visits:
the first visit after the night's refresh or an admin change, a team's first look at its pages, a pick nobody has
opened yet. In 2.7.4 the median first visit was 0.37 s, but at 365 days Agents in use, Access to labeled content and
Apps took 15 to 18 s and about a dozen pages took 6 to 12 s.

Proposed exceptions, which would be listed in Known limits rather than left unsaid: CSV downloads of full lists (the
bar is that the download *starts* within 1 s), and Admin pages that run checks when asked (Health, Directory quality's
tests).

## Why add 40 million older records

3.1.3 added the 730-day and All data periods. The 120M lab database (`CopilotScaleTest`) holds exactly one year,
2025-09-25 to 2026-09-24, so at 120M the two new periods read the same rows as 365 days. Nothing has tested two
years of history at scale.

The plan adds **40 million interactions for 2024-09-25 to 2025-09-24**, so the lab holds 160 million over two years:

- volume grows over that year the way a rollout does, from a pilot of about 20,000 a day to about 190,000 a day,
  meeting the current year's level;
- people grow from about 30,000 to the current 210,000;
- with matching records behind them: accessed resources (about 70 million more), sessions, licensed days and
  license changes;
- written at the `bi` level, as the 120M lab data was (see *What the lab holds* below), then the dashboard's own
  procedure (`dash.usp_RefreshDashboard`) builds its daily totals over both years.

### What the lab holds (found 2026-11-23)

- The 120M is synthetic and flat: about 10 million interactions and 207,000 people every month.
- `bi.FactInteraction` has 120M rows, but only 40M raw records sit under it (`audit.CopilotAudit`, all
  CopilotInteraction). The other 80M were written straight into `bi`. **A full `bi` rebuild would rebuild the fact
  from the 40M and lose the rest**, so the upgrade and the older year must never start one. The installer's triggers
  were checked: the record shapes, flatten mark, file types and UseKind pass. The one-time agent-id read could have
  triggered a rebuild, so its mark (`AgentIdsReadUtc`) was set by hand. The license step was skipped
  (`-SkipLicenseDatabase`): this lab uses one database for both, and the step would have shredded the 40M Copilot
  records as license events.
- 1% of accounts are disabled (2,163 of 210,000). Left as it is: the enabled-today check costs the same whatever the
  share.
- Scopes measured: the whole organization (`user00000`, 210,000), a large part (`user140000`, 70,000) and a team
  (`user00024`, 585, the team the 120M plan fixes were tuned on).
- The Workbench pages read their own governance database, not the interactions, so they are timed apart.
- Backup before any change: `CopilotScaleTest-before-3.1.4.bak` in SQL Server's backup folder (48 GB, with
  checksums, 2026-11-23).

The lab server is a hard test: 8 processors, 31 GB of memory, SQL Server 2025 Enterprise Developer at MAXDOP 6, and
a database of 114 GB now (about 150 GB after) that cannot fit in memory.

## Phase 0: get the lab ready

`CopilotScaleTest` is the lab database normally left alone, so this phase needs your OK.

1. Back it up, compressed (about 35 GB; the PC has 195 GB free). A second 160M database alongside is not possible:
   there is not enough disk.
2. Upgrade it from its 2.5-era schema to 3.1.3 with `Install-CopilotAudit.ps1 -Dashboard`. The change is additive,
   as every install over a release is. The leftover `perf.*` tables from October's discarded experiment stay as
   they are unless you say to drop them.
3. A new dev tool, `tools/dev/scale/Add-ScaleHistory.ps1`, writes the older year, then runs the normal history build.
   Expect several hours of machine time.
4. A new dev tool, `tools/dev/speed/Measure-PageSpeed.ps1`, opens every page at every period and scope: about
   2,000 first visits. The page list comes from `Nav.cs`. The dashboard runs with its query cache off
   (`Insights__QueryCacheMinutes=0`), so every request reads the database as a first visit does. That can only
   overstate a page that asks for the same query twice in one request. The tool writes a CSV of every visit, a summary
   of the pages over 1 s, and the statements that took longest in SQL Server (`sys.dm_exec_query_stats`).
5. **Baseline:** run it on 3.1.3 at 160M. Nothing gets fixed before the numbers say where the time goes.

## Baseline at 160M (2026-11-23)

3.1.3 on the lab with two years, every visit a first visit (query cache off). Full table:
[baseline-160m-summary.md](baseline-160m-summary.md).

- 2,375 visits in 120 minutes. Of the 2,276 that loaded, **1,128 took over 1 s**. Median 0.99 s, 90th percentile
  9.6 s, 99th 22.9 s, slowest 42.0 s (Agents in use, 730 days).
- **57 of 93 pages** went over 1 s at least once, **25 over 10 s**; 36 stayed under 1 s everywhere.
- By period, the median visit: 7 days 0.77 s, 30 days 0.82 s, 365 days 1.41 s, 730 days 2.26 s, All data 2.25 s.
  Short periods still have slow pages: Forecast, Budget and renewal, License pool and What to act on read a fixed
  window, so they are slow at 7 days too.
- By scope, the median visit: whole organization 1.80 s, the 70,000 part 1.08 s, the team of 585 0.30 s.
- The slowest pages: Agents in use 42.0 s, Apps 37.5 s, Agents costing credits 27.6 s, Forecast 27.4 s, What to act
  on 27.1 s, Oversharing hotspots 26.4 s, Leadership overview 23.7 s.
- Failures: 96 were the four old addresses of folded pages redirecting (expected). Three were real: New joiners'
  CSV link answered 404 at 7, 14 and 30 days.
- `queries.csv` holds only plans that stay in the plan cache, which leaves out every statement with
  `OPTION (RECOMPILE)` (each team's view). Query Store was turned on in the lab for the next runs.

## Phase 1: find where the time goes

Group the slow pages by the query they share. Most pages share the headline figures (`Data/Metrics.cs`) and the
period and part logic (`ReportContext`), so one fix there helps dozens of pages. Fix the shared queries first,
then the slowest pages one by one.

## Phase 2: the fixes, widest reach first

1. **Totals per person for each period.** One row per person per period choice, plus per app and per agent where
   pages need it, rebuilt each night after `bi`. A count of distinct people over 730 days or All data becomes a sum
   of a few hundred thousand rows instead of a scan of 160 million. *Needs your OK:* this is the idea of the 2.5.1
   experiment you discarded in October (still in `git stash`). It would reuse that work's ideas, not its code (44
   files against 2.5.1).
2. **"Enabled today" carried on those rows.** 3.1.3 added the check to every count of people
   (`ReportContext.IsEnabled()`), which joins the directory on every query. Stored on the totals, it costs nothing.
3. **A team's pages.** Scoping to a part goes through `dash.OrgClosure` (1.5 million rows). Check its indexes at 160M,
   and keep totals per large part (for example over 5,000 people) where the join stays slow.
4. **Pages still reading the big tables directly.** Agents, Apps, the agent workbook, Security, Governance and Regions
   still read `bi.FactInteraction` (120M rows) or `bi.FactAccessedResource` (209M) for some figures. Move them to the
   daily totals (`dash.UserDay`, `dash.ResourceDay`) or new ones. Both facts are already clustered columnstore, so
   the gain is from reading less, not from new indexes.
5. **Query plans at 160M.** `PlanHints.cs` pins plans that went wrong at 120M. Recheck each one at 160M, at MAXDOP 6.
6. **Time spent outside SQL.** 2.7.4 found pages spending 0.5 to 0.7 s building results with no query run. Profile
   whatever the baseline shows above about 0.2 s outside SQL.
7. **The 07:00 pre-cache.** It now builds eight periods, including 730 days and All data. Measure how long it takes
   at 160M and set a bar (proposed: done within 30 minutes), so leaders still find their pages built.

## Phase 3: keep it fast

- `Measure-PageSpeed.ps1` becomes a step in the release checklist (`tools/dev/README.md`). CI can't hold 160M rows.
- A source test lists the pages allowed to read the two big fact tables, so a new page can't slip back to scanning
  them without someone noticing.
- Schema changes stay additive (new tables and columns only), and the baseline is retaken at the release.

## Phase 4: release 3.1.4

Cut it through the usual flow. The install note for 3.1.3 users: run the installer once (it adds the new tables),
and the first nightly fills them, which adds some minutes to that night. Measure that night's extra time in the
lab and give it in the note.

Known limits to state: all figures are from synthetic lab data on one 8-processor server, not the client's data or
hardware (SQL Server 2022 CU26 Enterprise there), and any page that still misses the bar is named with its time.

## Risks

- Some pages may miss 1 s on a first visit even with the totals: a search across everyone's names over All data,
  or one person's two-year history drawn from raw records. They would be named in Known limits with their times,
  not hidden.
- The totals add to the nightly run and to disk. The baseline will show by how much before anything is committed.
- The pre-cache gets heavier with each period added. If it can't make 30 minutes, the order it builds pages in
  matters more than its total length.

## Decisions needed from you

1. OK to back up, upgrade and extend `CopilotScaleTest` (Phase 0)?
2. OK to bring back the per-person period totals you discarded in October (Phase 2, item 1)? This can wait for the
   baseline numbers.
