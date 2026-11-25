# Positioning plan: the AI ledger, money found, and data sources

Status: proposed, 2026-11-24. Nothing built. Three changes that make the product read as *the system of record for
AI use, cost and control*, not a Copilot usage dashboard. Small enough to ship with 3.3.0 (the Foundry and GitHub
pages need the Data sources page to show where they plug in), or as 3.3.1 straight after.

## 1. Name the category: "the AI ledger" (words only, no new data)

What changes is wording, in the places a newcomer reads first:

- **Site header and sign-in page**: under "Copilot Insights", the line *The ledger of AI use, cost and control,
  kept in your own SQL Server.* (setting `Insights:Tagline`, so a client can change or blank it).
- **Start page** (`Pages/Index.cshtml`): three panels above the list of reports, one per question, each with one
  figure and the pages that answer it. Each panel only shows when the viewer holds a section behind it.
  | Panel | Figure | Opens |
  |---|---|---|
  | What are we spending on AI? | spend a month, every area (Costs and savings' total) | Leadership › Costs and savings |
  | Is it worth it? | value delivered a month (hours), and the return | Costs and savings › Value; Adoption |
  | Are we in control? | open governance items: flags to triage, leavers holding licenses, agents without an owner | Governance, Workbench |
- **README, DASHBOARD.md opening, the pitch decks, the release notes' first line**: the same sentence, and the three
  questions as the way the product is laid out.
- **Not in this plan**: renaming the product. A name not tied to one vendor matters once Foundry and GitHub join,
  but it touches the installer, the IIS site, the service, the public copy (Logbook) and the client's scripts.
  Decided separately.

## 2. Money found to date (one figure, on the start page and Costs and savings)

The figures exist; this adds them up and names them consistently.

- **Two numbers, never mixed:**
  - *Saved* (done): what license exports already saved (`RealizedSaving`, the License action outcomes page),
    plus idle Foundry deployments and GitHub seats removed once 3.3.0 can see them go. Counted from the month the
    change was seen, a month at a time, so it grows.
  - *Found* (open): the savings Costs and savings finds today, by confidence (High only in the headline; Medium and
    Low behind the (i)).
- **Where**: the start page's top line for whole-organization viewers and the Licensing role ("US$X saved to date,
  US$Y more found"), a tile on Costs and savings, and the weekly email's first line.
- **A ledger of it**: `dash.SavingLedger` (month, area, kind Saved or Found, amount, source, evidence link), written
  by the nightly dash refresh from what the pages already count, so the history survives price changes and is
  queryable from Power BI. New table, additive only.
- **Rules**: a figure links to a page showing the same number (house rule); prices come from the assumptions
  register; a saving is counted once (a seat reclaimed is not also an idle seat found); suite moves are never
  counted (they stay a review).

## 3. A Data sources page (what the ledger covers)

Admin › Health and the Workbench's Sync and freshness already show runs to administrators. This is the reader's
view: what is connected, what that unlocks, and what is not connected yet.

- **Path**: `/sources`, under the start page's menu, open to anyone signed in (it names no people).
- **One card per source**: Purview audit, Microsoft Graph (directory, licenses), Copilot sessions, the activity
  database (AI apps, Power BI, Power Apps), Power Platform agents, Foundry, GitHub Copilot, the gateway (3.4.0),
  ITSM and mail (Workbench). Each shows:
  - status: connected, last load and its result, the newest day it holds, rows (from `audit.PipelineRun`,
    `audit.ExternalUsageRun`, `dash.RefreshRun`, the loaders' run tables)
  - what it unlocks: the sections and pages that read it, linked
  - when not connected: one line on what it would add and what it needs ("Foundry: Monitoring Reader and Cost
    Management Reader; see FOUNDRY-AND-GITHUB.md"), and the `-CheckOnly` command once 3.3.0 adds it
- **Coverage line at the top**: "7 of 10 sources connected; the AI bill covers Copilot, Copilot Studio, Power BI and
  Power Platform; Foundry and GitHub are not connected."
- Administrators keep Admin › Health for the detail (steps, timings, certificates); the cards link to it for them.

## Tests and docs

- xUnit: the three panels' visibility by grant, Saved versus Found never double-counted, coverage counts.
- SQL integration: `dash.SavingLedger` refresh is repeatable (same month twice gives the same rows).
- Layout audit (labels whole), figure-link audit (each new figure opens a page with the same number), schema
  baseline, DASHBOARD.md table (`/sources`), OPERATIONS.md (the tagline setting), CHANGELOG.

## Decisions for the user

1. **Release**: ship with 3.3.0 (recommended: Data sources is where Foundry and GitHub show they are missing),
   or as 3.3.1 after it?
2. **"Saved" includes only what the dashboard's own exports did** (recommended: provable), or any license removed
   that the dashboard had flagged, whoever removed it?
3. **Data sources open to everyone signed in** (recommended: no names, and it shows the platform's reach), or
   administrators only?
4. **Tagline wording**: "The ledger of AI use, cost and control, kept in your own SQL Server", or yours.
