# 3.0.0: Copilot Insights and the Governance Workbench as one product

Written 2026-11-02, after 2.8.0. It sits next to `plans/azure-paas/PLAN.md` and takes over parts of it (section 9).

## 1. The answer first

The merge is feasible. Both products use ASP.NET Core 10 Razor Pages, Dapper, SQL Server, xUnit and
Microsoft.Identity.Web 4.16.1, and they follow the same conventions. Their database schemas share no names.
The hard part is that both keep Copilot seat reservations and chargeback per business unit, in two different
models. 3.0.0 makes them one.

| | Copilot Insights (this repo, 2.8.0) | Governance Workbench |
|---|---|---|
| What it is | Measures Copilot use, licenses, costs and agents from audit records | Records who owns, funds and approved each Copilot workload, with evidence |
| Code | about 63,700 lines (no docs) | about 25,000 lines |
| Pages / tables / tests | 92 pages, 134 tables, 635 dashboard tests + 17 script suites | 50 pages, 85 tables, 207 tests |
| Background work | Nightly PowerShell pipeline (scheduled task) | .NET worker with 10 jobs (Windows service) |
| Sign-in | Windows (default) or Entra (2.8.0) | Entra only |
| Roles | 8 roles in `dash.RoleGrant`, profiles, groups, org-wide | Entra app roles (User, Central, Operator, Admin, Auditor), RACI, delegations, LOB seat admins, row-level security |
| SQL Server | 2017 or later | 2022 or later (ledger tables), temporal tables, row-level security |
| Status | In production at the client | Local only, never pushed, never deployed |

## 2. The shape of 3.0.0

- **One product, one site, one installer, two databases.** The Copilot database stays as it is. The Workbench's
  database becomes `CopilotGovernance` beside it, with its ledger tables, temporal tables and row-level
  security. The SQL Server 2022 requirement then applies only to the governance part.
- **Governance is optional.** It stays off until `Insights:Governance:Database` is set. Without it, 3.0.0
  behaves like 2.8.0, so a client can install 3.0.0 with no governance setup.
- **One role system: Insights'.** The Workbench's app roles, RACI positions and LOB seat admins become Insights
  grants (D4). So governance works with Windows sign-in as well as Entra.
- **One seat model.** `core.SeatReservation` is the record; `dash.Reservation` is hidden (D1).
- **The client's data stays.** Every table Insights already has at the client keeps its rows and its columns.
  Nothing is moved out of it or rewritten.

## 2b. The user's decisions (2026-11-02)

- Hide `dash.Reservation` and use `core.SeatReservation`.
- Convert the Workbench's role system to fit Insights.
- SQL Server is the same at the client: one SQL Server 2022 instance (16.0.4275.2, CU26, Enterprise,
  Windows Server 2022) holds both databases. The Workbench needs 2022 or later and uses nothing from 2025.
- Keep the data already in Insights; it is deployed at the client.
- Merge, and push to GitHub.
- **History kept.** The Workbench comes in with `git subtree` under `governance/`, so its 40 commits stay.

## 3. Decisions, with a recommendation each

| # | Decision | Recommendation | Why |
|---|---|---|---|
| D1 | Which seat model is the record | **Decided:** `core.SeatReservation` by line of business. `dash.Reservation` is hidden: its admin page and the leader-based view are off, its rows and columns stay as they are, and nothing writes to it any more. | It holds the client's own facts from 2026-10-29: 14 lines of business, AD tracking groups, the April 1 true-down window, Finance decides. |
| D2 | Separate database or one | Separate (`CopilotGovernance`), on the same instance | Ledger tables are append-only. Keeping them apart leaves the deployed Copilot database untouched. |
| D3 | Sign-in for governance | Windows or Entra, as for Insights | With Insights' roles (D4), a person is found by SID or object ID (2.8.0 matches both), so no Entra-only part is left. |
| D4 | One role system or two | **Decided:** Insights' system. Workbench roles become Insights roles and grants (section 3b). | One place to grant, one access check, profiles and groups for everything. |
| D5 | Background work | The Workbench's worker becomes the product's Windows service. The PowerShell nightly stays as it is in 3.0.0. | This is the first step of `azure-paas` Phase 4 without moving the loaders yet. |
| D6 | App registrations | Keep two: the pipeline's (audit read) and the governance worker's (users, groups, access reviews, mail, Teams) | Each permission set can be reviewed and granted on its own, which banks prefer. |
| D7 | Version number | 3.0.0 | A new database, a Windows service and a new sign-in requirement for a whole section. |

## 3b. Workbench roles in Insights' system

| Workbench | In Insights | Scope |
|---|---|---|
| `Workbench.User` (anyone signed in) | No grant needed: everyone can start a request and see their own | Own requests |
| `Workbench.Central` | New role `Governance` with Organization-wide scope (`OrgWide`, 2.8.0) | All lines of business |
| `Workbench.Operator` | New role `GovernanceOperator` | Fulfillment and verification queues |
| `Workbench.Admin` | Insights `Admin` (with `dash.RoleGrant.Pages` for chosen governance admin pages) | Configuration pages |
| `Workbench.Auditor` | New role `GovernanceAuditor` | Read-only, evidence and audit export |
| LOB seat admin (`LOB_SEAT_ADMIN`) | `Governance` granted for one line of business: new nullable column `dash.RoleGrant.FundingOwnerId` | One line of business |
| RACI positions and delegations | Stay in `sec.*`: they route work, not page access. Admin › Check someone's access shows them. | Per activity |

- The role CHECK on `dash.RoleGrant` and `dash.AccessProfileRole` is widened with the three new roles. A
  wider CHECK is allowed by the schema rule.
- Row-level security keeps working. On every governance connection, the site sets `SESSION_CONTEXT`: PersonId
  from `sec.Person` (matched by object ID or SID), IsCentral from an org-wide `Governance` grant, and the lines
  of business from `FundingOwnerId` grants.
- `sec.RoleAssignment` stays (its rows are ledger history) but is no longer read for page access.
- Access profiles get governance starters: "Seat admin for a line of business", "Governance operator",
  "Auditor".

## 4. Phases, order and size

S = up to a day, M = a few days, L = a week or more. Each phase ends with every test passing on both sides.

### Phase 0: ground work (S)

- `git subtree add --prefix governance <workbench repo> main`, so the history is kept.
- CI builds and tests `governance/CopilotGovernance.sln` beside the dashboard. Its integration tests run against
  a throwaway SQL database, like `Test-SqlIntegration.ps1`.
- The repo's checks cover `governance/`: spelling, PSScriptAnalyzer, no release numbers in comments, no error
  text on a page.
- Package versions lined up: the Workbench uses 10.0.13 for Microsoft.Extensions.* and Pkcs; the dashboard is on
  10.0.14. One `Directory.Packages.props` for both.
- Leave out of the repo: the walkthrough audio and screenshots under `governance/docs` (about 21 .wav files)
  and the test tenant's IDs (`config/tenant.test.json`). The setup scripts stay.

### Phase 1: one site (L)

- The governance pages move into the dashboard as a section of its own, "Governance", under `/governance/...`.
  The old Workbench addresses redirect, following the redirect rule for moved pages.
- Insights already has a "Governance" section (AI apps, DLP, controls). Name: "Ownership and approvals" for
  the new section, so both keep their meaning (ask the user).
- One layout, one menu (`Nav.cs`), one style sheet, info icons on every field. The Workbench's own layout and
  CSS go.
- The Workbench's services (`Governance.Application`, `Governance.Domain`, `Governance.Infrastructure`,
  `Governance.Notifications`) stay separate projects that the dashboard references. The web project goes.
- A second `Db` connection for the governance database. Every connection to it sets `SESSION_CONTEXT`
  (PersonId, IsCentral) as the Workbench does now, so row-level security keeps working.
- Its pages join `InsightsPage`, so they get the audit log, `Incident` references, privacy mode and the
  data stamp.

### Phase 2: Workbench roles converted to Insights' (L)

- Section 3b, built: the three new roles, `dash.RoleGrant.FundingOwnerId`, the grant form and Admin › Access
  profiles offering them.
- The Workbench's `AppRoleRequirement` and `PlatformGovernanceRequirement` are replaced by Insights'
  `RequiredRole` checks. Its policies go.
- `Viewer` gains `GovernancePersonId` (from `sec.Person` by object ID or SID) and the lines of business it
  may see.
- Admin › Check someone's access adds RACI positions and delegations.
- The Workbench's "act as" stays limited to the test environment (`Testing:AllowActAs`).

### Phase 3: one person list and one license read (M)

- `sec.Person` is filled from `dash.Person` by Entra object ID (2.8.0 added `dash.Person.ObjectId`), so there
  is no second directory sync. The Workbench's `DirectorySyncJob` keeps only what the Copilot database doesn't
  have: Entra-only accounts and managers.
- License holders: `SeatSyncJob` keeps reading the shared license group and each LOB's tracking group (Insights
  doesn't hold tracking groups). Who holds a Copilot license comes from `audit.CopilotLicense`, so it isn't read
  twice.
- "Reclaim inactive seats" uses Insights' activity (`bi` last activity per person) instead of
  `intg.CopilotActivity`. That table stays, filled from `bi`, so its readers keep working.

### Phase 4: one seat and chargeback model (L)

The biggest phase. With governance on:

- Leadership › Copilot reservations, Licensing › Reservation holders and Credit chargeback read lines of business
  from `core.FundingOwner` and reservations from `core.SeatReservation`. The leader-based view stays for clients
  without governance.
- `dash.Reservation`, `dash.ReservationRate` and `dash.ReservationSnapshot` are hidden (D1). They keep their
  rows and columns, as the schema rule and the client's data require, but no page or step writes to them.
  Their admin page goes behind the hidden-reports switch, with a note pointing to the governance seat pages.
- The client's existing reservation rows are offered as a one-time starting point for `core.SeatReservation`
  (Admin › Seat groups › "Start from Insights' reservations"). Nothing is copied unless an admin confirms.
- One monthly snapshot: `core.SeatChargeSnapshot` (FinOps certifies it).
- A new `bi`-side view, `dash.SeatReservationCurrent`, gives Power BI and Grafana the governance figures by
  line of business. It is a new object, so it doesn't change anything existing.
- The 95% purchase gate, the April true-down window and the single seat rate become one rule set, with tests on
  both pages so they show the same figure.
- Questions for the client (they block this phase): who pays for seats with no line of business, the seat rate,
  and whether an increase takes effect on approval or at the start of the next calendar month.
- Figure links: each linked headline figure on the seat pages opens a governance page with the same number.
  `Test-FigureLinks.ps1` covers it.

### Phase 5: one installer and the Windows service (M)

- `Install-CopilotInsights.ps1` gains `-GovernanceDatabase`. It runs the Workbench's ordered scripts
  (`Deploy-Database.ps1`), creates the `app_web`, `app_worker` and `gov_reader` users, and installs the worker as
  a Windows service.
- `Test-WorkbenchPrerequisites.ps1` runs first. It refuses a SQL Server older than 2022 and names what is missing.
- The ledger digest (`Save-LedgerDigest.ps1`) becomes a scheduled task the installer creates.
- Two web servers behind a load balancer (2.7.6): the worker runs on one server only. The installer's
  `-WorkerServer` switch chooses it; the jobs take a `sp_getapplock` lock, so a second copy waits.

### Phase 6: docs and the checks the repo enforces (M)

- `docs/OPERATIONS.md` gains every governance admin page and job (the Workbench's own OPERATIONS.md moves in).
  `Test-OperationsGuide.ps1` covers them.
- `docs/DASHBOARD.md` lists the governance pages. The Workbench's USER-GUIDE, RUNBOOK and DECISIONS become
  `docs/GOVERNANCE-*.md`.
- `docs/SCHEMA.md` gets a governance part, and the schema baseline covers both databases.
- From 3.0.0, the governance database follows the same rule: changes are additive only.
- CHANGELOG 3.0.0 with the install notes: SQL 2022 for governance, the second app registration and its
  permissions, the Windows service, the new packages for the client's package feed.

### Phase 7: release checks (M)

- Every test, the UI audit at 1440, 1024 and 390 wide, and figure links. Then the usability pass and an
  adversarial review across the merged code.
- Install over 2.8.0 with governance off: every page the same, schema compatible.
- Install with governance on, on this PC's test tenant (cloudsconnected.com). Try one request end to end: a
  seat increase from the line of business through Digital Workplace and FinOps, then seen on Copilot
  reservations.
- First real IIS install of the worker and the governance part. This is still untested from the Workbench.

## 5. What 3.0.0 needs from the client

- **SQL Server version:** known. SQL Server 2022 CU26 Enterprise (16.0.4275.2) on Windows Server 2022,
  the same instance for both databases. Nothing else needed.
- **One Entra app registration** for the governance worker (sign-in can stay Windows), with these app-only
  permissions:
  - `User.Read.All`, `GroupMember.Read.All`, `AccessReview.Read.All`;
  - entitlement management read;
  - mail to one shared mailbox, scoped with Exchange RBAC;
  - Teams messages to the governance channels;
  - Power Platform reader.
- **Packages on their internal feed:** the Workbench's (Microsoft.Extensions.Http.Resilience,
  Microsoft.Extensions.Caching.SqlServer, Microsoft.Identity.Web.UI, Microsoft.Extensions.Hosting.WindowsServices)
  plus 2.8.0's.
- **The three seat questions** in Phase 4.
- **Their ITSM** (ServiceNow): an endpoint and an account for the ticket poller. Until then, the governance
  part runs with tickets entered by hand.

## 6. Risks

| Risk | What reduces it |
|---|---|
| Untested Workbench code reaches a production site | Governance is off by default; installing 3.0.0 with it off is tested to change nothing. |
| Two seat figures disagree | One record (D1); the Insights pages read it; a test compares both pages. |
| The client's Insights data changed by the merge | Nothing is moved out of the Copilot database or rewritten; the 2.8.0 baseline check runs against it. |
| Row-level security bypassed by a shared connection | Every governance connection sets `SESSION_CONTEXT` read-only on open; a test opens a page as two lines of business. |
| Worker runs twice on two web servers | `-WorkerServer` plus an app lock per job. |
| Merge breaks Power BI or Grafana | `dash.*` tables stay and are filled as before; the schema baseline is checked against 2.8.0. |

## 7. Not in 3.0.0

- The Azure move (`plans/azure-paas` Phases 5–9). 3.0.0 lays the worker for its Phase 4.
- Porting the PowerShell loaders to the worker.
- Writing to Microsoft settings. The Workbench only reads, and stays that way.
- The open Workbench items: the Teams channel flow (needs a Teams connection made by the user) and
  `Setup-TestTenant.ps1 -Steps Seats` (needs the user's Edge sign-in). Both can be done during Phase 7.

## 8. Effects on the Azure plan

- Phase 3 (directory from Entra): the Workbench's `DirectorySyncJob` is a starting point.
- Phase 4 (the .NET worker): 3.0.0 brings the worker in; the loaders move into it later.
- The governance database uses ledger tables, temporal tables and row-level security, and Azure SQL Database
  supports all three, so it needs no change for Azure.
