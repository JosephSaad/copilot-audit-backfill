# Operations guide

Every admin page and every background job: what it is for, when it runs, how to run it again, and what
alert it raises. Keep this page current: add a row in the same change as any new admin page or job.

## Admin pages

Admin pages need the Workbench.Admin app role (granted through a PIM-eligible group in production), except
where noted. Administrators configure; they never approve business decisions or edit decisions (SoD5).
Changes to the RACI matrix and to control templates need two approvers, the Platform Owner and Steering
(SoD6). Every change is written to the audit trail.

| Page | What it is for | Who | Notes |
|---|---|---|---|
| Admin › RACI (`/Admin/Raci`) | Shows the active 19-activity matrix and the 12-row view generated from it; drafts a new version | Admin drafts; Platform Owner and Steering approve | A version activates only when every activity has exactly one A or A/R |
| Admin › Workflows (`/Admin/Workflows`) | Journeys, stages, transitions and consults per version | Admin, Platform Owner | A request stays on the version it was submitted under |
| Admin › SLAs (`/Admin/Sla`) | SLA policies, escalation ladders, holidays, business time zone | Admin, Platform Owner | Calibrate SLAs during the six-week validation |
| Admin › Notifications (`/Admin/Notifications`) | Rules N01 to N22, business wording, quiet hours | Admin; wording approved by the Platform Owner | Email is the notice of record |
| Admin › Roles (`/Admin/Roles`) | Role assignments with scope and dates; Entra group to role mapping | Platform Owner (central), business owners (LOB) | Every assignment is reviewed each year |
| Admin › Integrations (`/Admin/Integrations`) | Last run and health of every integration and job; settings and feature flags | Admin | The beta Cost Management API stays off (OD9) |
| Admin › Intake switch | Opens intake (B1) once setup steps 0 to 7 are done | Platform Owner | Closing intake stops new submissions only |
| Operations › Sync (`/Operations/Sync`) | Runs the Power Platform inventory sync with the operator's own sign-in | Operators | The inventory API accepts only a signed-in user |
| Operations › Drift (`/Operations/Drift`) | Findings: drift, unregistered items, vacancies, assignments without funding | Operators | Acknowledge, resolve or accept with a reason |
| Operations › Events (`/Operations/Events`) | Consumption events (J5) with acknowledgement targets | Operators | High and critical events open an ITSM incident |
| Operations › Attestations (`/Operations/Attestations`) | Attest Microsoft 365 settings with evidence after each change and monthly | Operators | Overdue attestations raise N22 |
| Reports › Audit (`/Reports/Audit`) | Decisions, evidence and the audit trail; export | Auditors | Read only |

## Background jobs

The worker runs these jobs. Jobs marked singleton take a SQL application lock, so two worker nodes never
run the same job at once. Each run is recorded in `intg.JobRun`; Admin › Integrations shows the last run.

| Job | Schedule | Singleton | What it does | Alert when it fails |
|---|---|---|---|---|
| Outbox dispatcher | Every 5 seconds | No (claims rows) | Delivers notices (email, Teams activity, Teams channel) and ITSM commands written by each change | Dead letter after the configured attempts, N19 to administrators |
| SLA timer | Every minute | Yes | Reminders at 50 and 80 percent of each task's SLA; escalation at 100 percent; closes returned requests after 10 business days | N19 after two consecutive failures |
| Directory sync | Every 15 minutes | Yes | People, cost center, division, manager, account status; governed group membership; leavers and role holders who moved raise vacancy findings (N20) | Staleness banner after two missed runs; N19 |
| Entitlement sync | Every 15 minutes | Yes | Access package assignments and requests for governed packages, recorded as decisions with source Entra; assignments without an active envelope raise N15 | N19 |
| Access review import | Daily during campaigns | Yes | Review decisions as recertification evidence | Campaign item stays open |
| Licensing read | Daily and on demand | Yes | Allocation, tenant-pool rule and billing policy per governed environment | Falls back to an operator-run read; N19 |
| Drift evaluator | After each read | Yes | Compares what was read with the approved posture and the register; raises findings (N10, N11) | N19 |
| ITSM status poller | Every 5 minutes for open tickets | Yes | A closed ticket moves the request to verification, never straight to completed | N19 |
| Campaign scheduler | Daily | Yes | Recertification, monthly re-attestation (N22), exception expiry (N13 at 30, 7 and 1 days), commercial renewals (N18 at 90, 60 and 30 days), inventory older than 7 days (N21) | N19 |
| Digest builder | Daily at 07:00 local time | Yes | One email per person with the items they are informed of (N17) | N19 |

To run a job again now, use **Run now** on Admin › Integrations. Jobs are idempotent: a rerun never sends a
notice twice (each notice has a de-duplication key) and never creates a second ITSM ticket (the adapter
searches by correlation ID first).

## Database

- Deploy or upgrade with `src\Governance.Database\Deploy-Database.ps1`; every script is safe to rerun and the
  schema only grows.
- Decisions, evidence metadata, attestations, verifications and the audit trail are ledger tables: rows can
  be added, never changed or deleted. Store the database digest on write-once storage.
- The web and worker connect as `app_web` and `app_worker`; neither is a database owner. Reporting tools use
  `gov_reader`.
- Row-level security shows each LOB only its own rows; central roles see all.

## Test environment tools

| Tool | What it does |
|---|---|
| `setup\Setup-TestTenant.ps1` | Registers the apps and creates the test groups, access packages, mailbox and Teams app; safe to rerun |
| `tools\Governance.Seed` | Seeds the demo institution and sample requests; a seeded database is left alone |
| `tools\Governance.MockItsm` | A local ITSM stand-in on port 5470 |
