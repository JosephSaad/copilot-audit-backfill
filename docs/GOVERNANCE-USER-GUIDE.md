# Governance Workbench: user guide

The Governance Workbench is part of Copilot Insights. Its pages are in the same site, under `/workbench`, in
the navigation's sections after the dashboard's: Ownership and approvals, Approval queues, Ownership registry,
Governance operations and Workbench settings. You sign in as you do for the dashboard. [DASHBOARD.md](DASHBOARD.md#governance-workbench-pages)
lists every page.

**Copilot seats only (3.6.0).** Unless an administrator has turned the whole Workbench back on (Workbench settings ›
Settings › *What the Workbench is used for*), the Workbench does one thing: it shows each line of business's Copilot
license holders and how they use Copilot. Its menu has one section, *Copilot Seat Management*; the journeys
below (requests, approvals, workloads, envelopes, recertification and the rest) are not offered, and their addresses
open Copilot seats. The LOB seat admin and FinOps sections below still apply; a line of business is active once it is
added, with its AD tracking group set on Seat groups.

Everyone starts on **Start** (`/workbench/Start`), which shows only the journeys your roles allow, and
**My work** (`/workbench/MyWork`), which lists your open tasks by due time. Every decision happens in the
Workbench, never by replying to an email or a Teams message. Each field has an (i) icon that says why it is
asked.

**Two kinds of access.** Which sections you see comes from your grants in the dashboard's Admin › Access:
nothing for the pages everyone has, **Governance** for the central teams' pages, **GovernanceOperator** for
operators, **GovernanceAuditor** for auditors, **Admin** for the settings. What you may do on a page (decide,
record, see one line of business) comes from your role in the Workbench: a central role given on Workbench
settings › Workbench roles, or a role for one line of business given on its funding owner's page. Your
Workbench roles shows yours. To name someone who is not in a picker's list yet, use *Or search everyone in Active Directory* under it: anyone in the organization's directory can be found and chosen. A page you may not open says *This page is not available to you* and who grants
it.

## Employee (requester, maker, developer)

- **Start here:** Start › *Use an approved Copilot service* for Copilot Chat or Cowork, or *Build or
  register an agent* for something new.
- **You do:** answer the questions, name a sponsor and an estimate, fix what comes back if a request is
  returned, upload test evidence when asked.
- **Never here:** choose a billing route or change billing settings; approve your own request.

## Workload sponsor

- **Start here:** My work, and the requests you sponsor.
- **You do:** confirm purpose and outcome; you receive returns and decisions.
- **Never here:** approve a request you sponsor through someone else's role.

## Product or solution owner

- **Start here:** Start › *Review what I own*, *Change capacity, a limit or an envelope*, *Retire a workload*.
- **You do:** keep classification inputs, support contact and outcome evidence current; plan retirement.

## LOB delegated approver

- **Start here:** My work: routine approvals for your LOB.
- **You do:** approve within your delegation and the envelope, or decline with the reason and the evidence
  needed. Every approval has an expiry or a review date.
- **Never here:** decide your own request; approve above your delegated limit (it escalates to the business
  owner).

## LOB business owner (or shared-service owner)

- **Start here:** My work and the LOB summary.
- **You do:** approve envelopes and delegations, authorize overage, accept production and continuity,
  recertify and retire your workloads. You are the last escalation step for your LOB's approvals.
- **Copilot seats:** Ownership registry › Copilot seats › your LOB shows the reservation against actual seats.
  Answer the **Seats proposed for reclaim** list: Keep, or Reclaim with a reason (a delegated approver can
  answer too). Reclaim opens an ITSM task to remove the person's OIM role; the seat shows as removed after the
  next license read. Name the LOB's first seat admin in the **People for this line of business** panel, where the
  business owner can also be changed later.
- **Never here:** configure Microsoft settings; record a risk acceptance or purchase on a request where you
  recorded the budget decision.

## LOB seat admin

- **Start here:** Ownership registry › Copilot seats, then your LOB.
- **You do:** track your LOB's Copilot seat reservation against the seats people actually hold: reserved,
  assigned, over the reservation (charged on top), unused reserved seats (still charged until the April EA
  anniversary), inactive holders and the month-by-month chart. Ask for a change with **Change the
  reservation**. Name other seat admins for your LOB in the **LOB seat admins** panel (type a name and pick
  it), or end one.
- **Never here:** see other LOBs; give any other role; name yourself; remove yourself when you are the last
  seat admin (name another one first, or ask the business owner). People get or lose a seat through the OIM
  role "<LOB> . M365 Copilot License . Role", not here.

The dashboard shows the same seats: Leadership › Copilot reservations, by line of business, with each figure opening the page here that shows it, and Licensing › Reservation holders, the people in each tracking group (for the Licensing role).

## LOB finance partner

- **Start here:** your LOB's cost centers and envelopes.
- **You do:** validate cost centers and envelopes; answer consults.

## Intake analyst (Governance)

- **Start here:** Approval queues › Intake.
- **You do:** check completeness, sponsorship and duplicates (B2); prepare the ITSM task from the template
  (B10).
- **Never here:** prepare fulfillment for a request you approved.

## Architect (Governance)

- **Start here:** Approval queues › Classification and Specialist reviews.
- **You do:** classify workloads (four independent fields, trigger type, meters) and review what the patterns
  do not cover.

## Platform operator (GovernanceOperator)

- **Start here:** Approval queues › Fulfillment and verification, and Governance operations.
- **You do:** configure in the admin centers through the ITSM task; verify someone else's change by read-back
  or attestation, including the deny case; run the inventory read on Sync and freshness (it needs your own
  Microsoft Entra sign-in); respond to consumption events; attest Microsoft 365 settings monthly on
  Attestations.
- **Never here:** verify your own configuration; verify a request you approved.

## Risk reviewer (Governance)

- **Start here:** Approval queues › Specialist reviews (Security and Risk).
- **You do:** review triggered requests and record risk acceptance, with conditions and expiry.

## FinOps lead and analyst (Governance)

- **Start here:** Approval queues › Specialist reviews (FinOps), Ownership registry › Envelopes and
  Commercial.
- **You do:** review forecasts and overages, manage envelopes and cost-center mapping, prepare commercial
  recommendations (J8), close commitments at retirement.
- **Copilot seats:** Ownership registry › Copilot seats shows purchased, reserved, buffer and the seats still
  free to reserve. Record each LOB's first reservation on its page (once; every later change is a seat
  reservation request). The FinOps lead certifies each month in Governance operations › Seat chargeback:
  check the draft (reserved, assigned, over, charged seats at one rate), then **Certify this month**. A
  certified month never changes. The "No LOB" row is seats nobody is charged for: get them attributed through
  the right OIM role.
- **Never here:** buy or renew anything; forecasts never trigger purchases.

## Commercial approver and sourcing agent (Governance)

- **You do:** the commercial approver approves P3 and pack purchases and renewals; sourcing executes the
  approved purchase and uploads the order as evidence.

## Platform Owner and Steering

- **You do:** own the catalog, templates and the Workbench; approve RACI and template versions together
  (two different people); open intake once setup is done. You reach Workbench settings › RACI, Intake switch,
  Workbench roles, Wording and Seat groups without the Admin role.

## Workbench administrator (Admin)

- **You do:** configuration, integrations, notification wording and roles, on the Workbench settings pages.
  An Insights administrator is a Workbench administrator.
- **Never here:** approve business decisions or change decision records.

## Auditor (GovernanceAuditor)

- **You do:** read decisions, evidence and the audit trail on Governance operations › Audit trail, and export
  them. Nothing can be changed from your role.
