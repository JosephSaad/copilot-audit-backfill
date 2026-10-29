# Decisions

Decisions taken for the build, including the architecture's open decisions (section 15.2) and where the
build differs from the architecture, with the reason.

## Open decisions (architecture section 15.2)

| ID | Decision | Taken | Notes |
|---|---|---|---|
| OD1 | SQL Server version | SQL Server 2025 (2022 minimum) | Ledger and temporal tables; `nvarchar(max)` with `ISJSON` keeps 2022 working |
| OD2 | LOB and cost-center master data | Finance feed for cost centers (CSV import page); Workbench for funding owners and roles; Entra `employeeOrgData` for user attribution | |
| OD3 | ITSM product and API | Test: built-in mock ITSM with a ServiceNow-style table API; production: OAuth client credentials with a scoped integration account | Adapter interface `IItsmClient` |
| OD4 | Access package design | One package per LOB per tier | Test tenant: `Copilot Chat - RETAIL - Standard` etc. |
| OD5 | Evidence retention | Configurable (`Evidence:RetentionYears`, default 7); content purge keeps hash and metadata | Confirm with Records Management |
| OD6 | Microsoft or partner access | None; the Platform Owner uploads evidence | |
| OD7 | After-hours coverage | 24x7 only for critical shared services (clock SLA on CRITICAL events) | |
| OD8 | Agent 365 licensing | Not licensed: registry export upload (X7); Graph adapter interface kept | |
| OD9 | Beta Cost Management API for evidence | Off (feature flag `Features:CostManagementBetaEvidence`) | Never drives a control (C20) |
| OD10 | Copilot Agent Kit | Not used; workloads keep an optional compliance case reference | |
| OD11 | Notification mailbox and Teams app | Test: `workbench@cloudsconnected.com` shared mailbox; Teams app "Governance Workbench (Test)" installed for the admin | |
| OD12 | Envelope units | Copilot Credits in Phase 1; units are typed so currency can follow | |
| OD13 | Sourcing column differences between the two RACI files | The 19-row matrix is the source; the 12-row view is generated from it | See below |

## OD13: the two RACIs, row by row

Columns: Business owner, Digital Workplace (Platform owner), Finance/FinOps, Architecture, Eng/Ops,
Security/Risk, Sourcing. A dash is Informed (blank in the deck). The 19-row values are merged with the deck's
own rule: where merged activities differ between C and I, show C.

| # | Simplified row | Deck | 19-row merged | Result |
|---|---|---|---|---|
| 1 | Business case and measurable outcome | A/R C C C - - - | same | match |
| 2 | Funding, annual budget and overage authorization | A/R C R C C C C | same | match |
| 3 | Enterprise Shared continuity acceptance | A C C C R C - | same | match |
| 4 | Lifecycle recertification and retirement | A R C C R C **-** | A R C C R C **C** | Sourcing **C**: retirement can end packs, P3 or PAYG scopes |
| 5 | Classification, routing and platform feature approval | C A C R C C C | same | match |
| 6 | Environment and policy pattern | - A C R R C **C** | - A C R R C **-** | Sourcing **I**: configuration; billing scope goes through rows 2, 5 and 9 |
| 7 | Technical implementation, monitoring and response | - A C C R C **C** | - A C C R C **-** | Sourcing **I**: operations; a C would add a consult task to every fulfillment |
| 8 | Microsoft capability evidence validation | - A C R C C **C** | - A C R C C **-** | Sourcing **I**: Architecture's technical judgement |
| 9 | P3 evaluation and purchase recommendation | C C A/R C - C R | same | match |
| 10 | Forecast, estimator, credits-versus-license review | C R A/R C C C C | same | match |
| 11 | Cost-center mapping, showback, chargeback, reconciliation | C C A/R C R - C | same | match |
| 12 | Security and risk acceptance | C C - C C A/R **C** | C C - C C A/R **-** | Sourcing **I**: Security's decision |

Reason for the rule: in the Workbench a C opens a consult task with a due date that can hold a stage; an I is a
line in the daily digest. Sourcing is consulted where a contract or commitment can change. Every row has one A;
merged rows share the same A and R; purchase and renewal stay separate rows (Commercial authority A, Sourcing R).

## Where the build differs from the architecture

| Area | Architecture | Build | Why |
|---|---|---|---|
| Data access | EF Core for commands, Dapper for queries | Dapper throughout, hand-written SQL | Ledger, temporal and RLS objects are clearer in explicit SQL; one data access style |
| Schema source | SDK-style SQL project producing a DACPAC | Ordered idempotent scripts (`src/Governance.Database/Scripts`) and `Deploy-Database.ps1` | Ledger tables and DACPAC extraction have known limits (R31); scripts rerun safely |
| Classification columns | NOT NULL on `core.Workload` | Nullable, enforced by `ClassificationComplete` at B3 | Discovered workloads exist before they are classified |
| Local hosting | IIS, gMSA, LocalMachine certificates, HTTPS | Kestrel on `http://localhost:5450`, worker as a console process, CurrentUser certificates, SQL logins | No admin rights on the test PC; the localhost HTTPS certificate is not trusted and trusting it changes a security setting. Production settings are unchanged |
| Spelling | British ("licence") | American ("license") in code and UI | House style of the user's other repositories |
| Test persona switch | not in the architecture | Development-only "Act as" switch, audited, off unless `Testing:AllowActAs` is true and the environment is Development | One real account in the test tenant has to play every role, and segregation of duties needs different people |

## Fixes from the core review (2026-10-24)

| Area | Decision |
|---|---|
| Sponsor | The sponsor is excluded from deciding the request they sponsor, like the requester (SoD1 widened): a sponsor owns the outcome claims. |
| Envelope commitment | A monthly estimate is committed for the months it runs inside the envelope period (capped by the requested duration), not once. Only Copilot Credit envelopes count in Phase 1 (OD12, E8). |
| Frozen versions | Active or retired RACI versions and control templates cannot change in place (database triggers); a change is a new version with two approvers. |
| Configurer | If the person who closes the fulfillment ticket approved the request, the change goes back to fulfillment with an SoD finding (SoD2). |
| Exit rules | B2 completes only when the completeness check passes; D1 passes only with a passing verification recorded by the verifier since the ticket closed (P5). |

## Production deployment (deploy/)

| Area | Decision | Why |
|---|---|---|
| Ledger digests | `deploy/Save-LedgerDigest.ps1` writes one digest file per run to a WORM share every 4 hours; `-Verify` checks the database against all of them | On-premises SQL Server stores digests automatically only in Azure immutable storage; a file share on write-once storage keeps the evidence outside the database (R27) |
| Snapshot isolation | The DBA sets `ALLOW_SNAPSHOT_ISOLATION ON` on the database | `sys.sp_verify_database_ledger` refuses to run without it; `Deploy-Database.ps1` sets only `READ_COMMITTED_SNAPSHOT` today (candidate for the deploy script) |
| SQL certificate validation | Production runs `Deploy-Database.ps1 -TrustServerCertificate:$false` | The script's default trusts any server certificate, which suits only the local test server |
| Mirror guard | The installers mirror files with robocopy only into an empty folder or one marked `.cgw-install` by an earlier install of the same kind | `/MIR` deletes what is not in the source; a wrong path must not wipe another application's folder |
| Settings | Installers refuse a settings file with placeholders, a password, a client secret or any `Testing` switch turned on | No secrets in files; test switches never reach production |
| ITSM credential | Certificate-based OAuth where the ITSM supports it, otherwise the institution's vault injects `Itsm__ClientSecret` into the service environment | Keeps the only possible secret out of files and source |

## Demo seed choices (test environment only)

| Item | Choice | Why |
|---|---|---|
| Business calendar | Canadian federal statutory holidays, with the observed Monday when a holiday falls on a weekend; time zone Eastern | The demo institution is a Toronto bank; replace with the institution's own calendar in Admin › SLAs |
| Envelope period | The calendar year | The seed must produce envelopes active today; set the institution's fiscal year in production |
| The real administrator | Workbench administrator and auditor only | Administrators cannot decide (SoD5); business personas are used through the test-only Act as switch |
| Personas | 46 fictional people, `@example.invalid` mail, flagged synthetic | No real personal data in the demo |

## Journey J9: change a Copilot seat reservation (2026-10-29)

| Area | Decision | Why |
|---|---|---|
| Who decides | The FinOps lead decides at S4 (decision type Budget approval); Digital Workplace (Platform Owner) is a blocking consult; the LOB business owner is a non-blocking consult | The reservation policy puts approval with Finance after Digital Workplace checks unallocated seats, pending demand and the buffer (Section 4.4); the LOB asks, so its owner is heard but does not approve its own demand |
| RACI | The existing Funding and annual budget row (FUNDING_BUDGET) is reused; the stage names the decider explicitly | No new RACI activity, so RACI version 1 stays active and unchanged |
| Who may ask | The LOB's business owner, delegated approver, finance partner or a product owner in that LOB | A reservation is the LOB's commitment; makers ask their LOB, and people get seats through the OIM role, not this journey |
| Current seats | Taken by the Workbench from core.SeatReservation when the draft is saved and again at submission, never from the form; the kind (new, increase, true-down) follows from it | The record must show the true previous quantity (CAL-06) |
| Decreases | Can be approved only from Seats.TrueDownWindowDays before the EA anniversary up to the anniversary; outside it the decider holds (or declines), and the held decision task is due when the window opens, so its reminder is the nudge | CAL-04: a decrease outside the anniversary does not change the baseline. A hold reopens S4 with a fresh Digital Workplace consult, because the seat picture may have changed by then |
| Effective dates | Approved true-downs take effect at the anniversary (the current row keeps running until then); increases on the decision date or the next month (Seats.IncreaseEffective) | CAL-05 and FIN-05; Section 6.10 leaves the increase rule to Finance |
| Flex-pool review | Required text for any approval; stored on the request and on the new reservation row | CAL-06 |
| Above unallocated | Allowed only with conditions stating the purchase need; the Workbench adds that a commercial decision (J8) is needed | Finance may buy more seats; the Workbench never buys (C21) |
| Two changes | A change dated later than another blocks the earlier one until that date; two changes taking effect the same day replace that day's row (the older version stays in the temporal history) | Reservation rows never overlap, so every count of seats in effect stays exact |
