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

## Demo seed choices (test environment only)

| Item | Choice | Why |
|---|---|---|
| Business calendar | Canadian federal statutory holidays, with the observed Monday when a holiday falls on a weekend; time zone Eastern | The demo institution is a Toronto bank; replace with the institution's own calendar in Admin › SLAs |
| Envelope period | The calendar year | The seed must produce envelopes active today; set the institution's fiscal year in production |
| The real administrator | Workbench administrator and auditor only | Administrators cannot decide (SoD5); business personas are used through the test-only Act as switch |
| Personas | 46 fictional people, `@example.invalid` mail, flagged synthetic | No real personal data in the demo |
