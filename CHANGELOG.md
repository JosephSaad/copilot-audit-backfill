# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed
- **Pages built over everyone are kept once worked out.** A stress test on 120 million interactions (210,000 people) found
  four pages at 0.55 to 0.75 s from cache with no query run: the cost model, the credit estimate and suite fit built an
  object for every person from rows already kept, on every visit. They are now kept under the data's stamp, the period,
  the part of the organization and the versions of the tables that change during the day. From cache at 120M: What to
  act on 0.70 s to 0.01 s, Costs and savings 0.6 s to 0.006 s, Credit use 0.6 s to 0.01 s, Suite fit 0.63 s to 0.14 s.
- **Counted per key, then named.** Apps and agents' most-used agents grouped every interaction by the agent's name with a
  distinct count of people: 13.7 s on a first visit for 90 days at 120M, now 1.6 s for the same rows. Its apps by week,
  Engagement's people per app, Compare two teams' apps, Working patterns' apps by hour and Sensitive content's labels over
  time are counted the same way; Sensitive content also reads its six sources together.
- **Ten more tables download as CSV**: Scorecard, Organization, Departments, New joiners, Targets, Drops and spikes,
  Labeled content by unit, Agents in use, Engagement and Apps.
- **Organization and Departments open with figures**: how many units or departments, and the highest and lowest adoption
  among those large enough to rank.
- Copilot on non-human accounts says *Name reads like an account* in its figure, as its filter and its list do.

### Fixed
- **Two whole-organization figures lost the whole organization when they linked.** Budget's *Seats to renew* and Credit
  commitment's *Drawn so far* are the tenant's on any team, but opened License optimization and Credit chargeback for the
  team shown. They now open them for the whole organization, as Costs and savings' links do.
- **Reservation holders, opened from the menu, only said to go and choose an organization elsewhere.** It now lists the
  organizations to choose from.

## [2.7.3] - 2026-10-21

The activity database's history no longer spends hours on a table nothing reads, and a database the installer
creates gets MAXDOP set for it. Then twenty passes over the dashboard: the slowest pages the first time they open,
figures that opened a page without their number, figures that said nothing of what they count, and the wording.

**Install over 2.7.2:** run `Install-CopilotAudit.ps1 -ActivityDatabase <name>` from this release; it replaces the merge
procedure. A history load already running finishes as it began. Then build and deploy the dashboard from this release
(docs\BUILD-FROM-SOURCE.md); nothing in its schema changes.

### Changed
- **Leadership › What to act on loads faster.** It read its six sources (the action counts, non-human accounts, units,
  targets, the cost figures and the security trend) one after another; it now reads them at the same time. On the demo
  data, a period not yet kept loads in 0.6–0.8 s instead of 0.9–1.2 s. Leadership › Overview, Organization and Apps
  and agents read their sources together in the same way.
- **Licensing › Swap list loads in a tenth of the time.** It read both sides (idle licenses and people wanting one)
  twice: once for the pairs and again for the four figures. One read now gives both; the figures are unchanged.
- **More pages read their sources together:** Security, Licensing, License overlap, Use cases and Scorecard. Their
  figures are unchanged; the gain shows on a large organization's data, where the reads overlap.
- **One separator between a section and its page.** Screens, mail and tips wrote both "Admin > Targets" and
  "Admin › Value names"; all now use ›, as the guides do.
- **Leadership › Apps and agents opens with figures:** interactions, the share in Copilot Chat and the share through
  an agent. Overview's Interactions opens it, and now finds the same number there.
- **Pages that were slowest the first time they open** (the demo data, a period not yet kept; the figures are unchanged,
  each compared with the previous build):
  - Governance › Copilot governance: 0.6–2.2 s to 0.25–0.45 s. Its seven reads start together, and Copilot by product
    classifies each person, app and agent once instead of every interaction.
  - Licensing › Credit use: 2.1–2.5 s to 0.25–0.3 s. The credit estimate grouped the interactions two ways at once
    with a distinct count, which read them twice and spooled them; one pass to a row per person and agent now gives
    the same rows. Agents costing credits, License for credits and Pay-as-you-go read the same estimate.
  - Enablement › Training effect: 1.1–1.8 s to 0.2–0.4 s, counting each person's active days in one range join on the
    day instead of two lookups a person.
  - Governance › AI apps, Apps and Enablement read their sources together; Enablement builds its population of
    licensed or active people once instead of three times.
- **Every headline figure says what it counts.** 41 figures on 17 pages had a label and a value and nothing else; each
  now has a note on the card or behind its information icon, and a test fails a new figure added without one.
- **Power users** (Enablement) and **Managers using Copilot** (Workforce) open the page that lists them.
- **Countries and offices' Highest and Lowest adoption** now say *Country with the highest (lowest) adoption*, beside
  the leaderboard's group of the same name.
- **Setup messages say who does what, the same way:** "An administrator sets this up by running
  Install-CopilotAudit.ps1 -Dashboard from <release> or later." Four told the reader to install a schema, one named an
  internal SQL file.

### Added
- **The installer sets MAXDOP on each database it creates**: the SQL Server's logical processors less two (at least 1),
  as a database-scoped configuration, so a large history's shred and the nightly refresh run in parallel and leave the
  server room. `-MaxDop <n>` sets it on every database it installs, existing ones included; `-MaxDop 0` leaves them
  all as they are. A database that exists keeps its own setting unless `-MaxDop` is given. Reading the processor count
  needs VIEW SERVER STATE; without it the installer says so and sets nothing.
- **`tools/dev/ui/Test-FigureLinks.ps1`**: opens the page behind each headline figure that is a link and checks it
  shows the same number, at as many periods as given. Part of the release checklist in `tools/dev/README.md`. On the
  demo data: 264 linked figures at six periods, none missing from its page; 26 lead on purpose to a related page in
  another month or unit (a month's credits to the people who used them) and are listed in the script.

### Fixed
- **An activity database's history spent hours building a table nothing reads.** Its `-Finalize` shredded every record's
  JSON into `audit.CopilotInteraction`, the Copilot reporting table, as a Copilot database's does. Nothing in the activity
  database reads that table: its records are shredded into their own tables by `audit.usp_ShredActivity`, which follows.
  At the client, 32 million records parsed at about 2 million an hour, which added roughly 16 hours to the history load.
  An activity database (one with `audit.usp_ShredActivity`) now leaves the table empty, at `-Finalize` and in each
  night's merge. The Copilot and license-event databases are unchanged.
- **Headline figures that opened a page without their number** (the rule since 2.6.7 is that a figure opens a page
  showing it):
  - Adoption's *Have started*, a share, opened the list of those who have not. That list's own count is now a figure
    of its own, from the same query, and carries the link.
  - Sensitive content's *People* counted those whose use touched labeled content; the list it opens also holds people
    only blocked from content (1,902 against 1,903 on the demo data). Both now count the same people, and the figure
    says so. *Blocked from content* now opens Security, whose *Blocked resources* is the same number.
  - Security's *Jailbreak attempts* and *Prompt injection detected* opened the flags of all three signals; each now
    opens the flags of its own kind.
  - Agents › Agents costing credits summed its estimate by agent, a few credits from Credit use's total that it opens;
    it now totals from people, as Credit use does.
- **Two chart labels left the chart on a phone:** a heatmap's row names (Drops and spikes) and a forecast line's end
  label (Forecast).
- **Licensing › Chargeback lost the licenses on disabled or missing accounts.** Its groups are enabled accounts found in
  the directory, so a license held by anyone else was paid for and charged to no group, and the page's total fell short
  of the licenses held (2,657 against 2,718 on the demo data). A figure, *Charged to no group*, now shows them with
  their monthly cost and opens Licensing › Leavers, which lists the same accounts. The total beside it is now named
  *Licenses charged to groups*.

### Known limits
- **Page times are the demo data's** (456,000 interactions on one SQL Server with 8 processors), cold, with no
  pre-cache. Each query rewritten for speed returns the same rows as before on the demo (compared in SQL and on the
  page at 7 to 365 days); the times at the client's 120 million interactions are not measured.
- **Not run at the client.** The activity shred's skip is proven by the SQL integration test and the source checks,
  not on the 32-million-record history; MAXDOP is set only on databases the installer creates, and only where its
  login holds VIEW SERVER STATE.
- **Power BI Desktop was not opened.** `powerbi/generate_model.py` writes the same model as 2.7.2; no `bi` view changed.
- Five figures lead on purpose to a related page that shows another month or unit (a month's credits to the people
  who used them, the seats to renew to the right size); `tools/dev/ui/Test-FigureLinks.ps1` lists them.

## [2.7.2] - 2026-10-20

Copilot Credits charged to each line of business, and a prepaid credit commitment tracked against its use: the
measurement a two-path Copilot Credit operating model (LoB Consumption and Enterprise Shared) and a Copilot Credit
Pre-Purchase Plan (P3) need.

**Install over 2.7.1:** run `Install-CopilotAudit.ps1 -Dashboard` from this release. It adds three tables
(`dash.CreditRoute`, `dash.CreditChargeSnapshot`, `dash.CreditCommitment`), one procedure and two settings; nothing else in
the schema changes. Then route the environments and record the commitment (docs\OPERATIONS.md, Copilot Credit chargeback
and commitment).

### Added
- **Leadership › Credit chargeback.**
  - Each line of business's Copilot Credits a month, by the path its environments are routed to: LoB Productivity, LoB
    Business Application, or Innovation. Enterprise Shared, and credits from environments not yet routed, are shown apart.
  - Each environment's credits, prepaid and pay-as-you-go, with what is charged at the charge rate and what Microsoft
    billed.
  - Exceptions: environments with no route, routes naming an Org ID not in Copilot reservations, and agents built by one
    line of business in another's environment.
  - Month-end snapshots: every complete month not yet kept, once the consumption report reaches its last day; never
    rewritten, except when an administrator takes a month again after routing its environments (audited).
  - For administrators: the routing register (one row at a time, or pasted from a spreadsheet, all checked first) and the
    charge rate.
- **Forecasting › Credit commitment.** A prepaid commitment such as the P3:
  - drawn, remaining and the burn rate;
  - when it runs out at that rate, or what it leaves unused;
  - drawn against an even plan;
  - the environments and lines of business drawing most.

  It is an estimate from the consumption report until Microsoft's applied figure is loaded.
- **An alert** when the commitment runs out, or is projected to, more than 30 days before its term ends, or to leave more
  than 30 days' worth unused, a commitment nothing draws on included (`CreditCommitmentAlertDays`, set on the page). It goes
  to `AlertLicensingTo`, once a week while it lasts. Terms may not overlap.
- Every figure on both pages says how it is worked out. Credit use, Agents costing credits, Copilot reservations and Costs
  and savings link to them. The charge rate and the warning days are in the assumptions register.

### Changed
- **The license cost of a period is whole calendar months** (Value and time saved, Board pack, the return on license
  cost). A period charges the calendar months whose last day falls inside it, each at the license price for everyone who
  held a license on at least one day of that month. So every figure is a multiple of the price (18, never 17.75 or 18.35),
  and the last 30 days charge one month. A period under a month charges none and shows no return. It was each day held
  at a 365th of a year. On the demo the 30-, 90- and 180-day costs move by +3.5%, +0.6% and −4.9%.
- Costs and savings no longer suggests buying Copilot Credit packs for metered credits while a commitment is in force:
  they draw on what is already paid for.
- The license pool alert names products, not part numbers.

### Fixed
- The operations guide said the activity database's history covered three record types; it covers those in
  `ActivityRecordTypes`, four by default.

### Known limits
- **Not run against the client's tenant.** An environment is matched to its route by the name the consumption report
  gives it. A billing policy that spans environments is entered on each of them.
- **The commitment's draw is an estimate**: the metered credits × what one credit draws. Microsoft's applied figure is
  in Azure Cost Management, which this release does not load.
- The wrong-environment check needs the agent inventory (its environment and owner) and a reservation register whose
  leaders cover the owners.

## [2.7.1] - 2026-10-20

The last three items of 2.7.0's usability pass: one renewal figure, product names in place of part numbers, and the
bridge from licenses bought to licenses held.

**Install over 2.7.0:** run `Install-CopilotAudit.ps1` from this release. It adds one table (`dash.SkuName`) and names
four app codes; nothing else in the schema changes.

### Fixed
- **Licenses on disabled accounts counted people whose Copilot comes only with a suite.** The check for
  `dash.CopilotSuiteOnly` looked for a table, and it is a view, so the leavers never left those people out. Their
  Copilot cannot be taken off alone. The check now finds views as well as tables.
- **Budget and renewal and License optimization disagreed on the seats to renew** by hundreds of seats. Budget took
  today's seats less revocations and leavers; Optimize took a forecast with a fixed 10% margin. Both now use one figure:
  - the licensed people forecast to use Copilot each week in 13 weeks, or the last 30 days' users if there are more;
  - plus the spare share kept on purpose (*Spare seats kept unassigned*, as Costs and savings and License pool use it);
  - plus the heavy Copilot Chat users with no license.

  Budget's *Seats to renew* is that figure less the people who use Copilot only through a suite. It opens License
  optimization.

### Added
- **License inventory › From licenses bought to licenses held**:
  - the add-on licenses bought, less those not assigned;
  - plus the people who hold Copilot only through a suite;
  - plus the rest of the difference;
  - to the people licensed today.

  It says which count Chargeback and Copilot reservations use.
- **Product names for licenses.** Pages say *Microsoft 365 E5*, not `SPE_E5`. This covers License inventory, Unused
  licenses, Pay-as-you-go, Overlap, License pool (with the part number beside it) and the Licensing digest.
  - The install brings Microsoft's names for 35 common licenses.
  - **Admin › Value names › License product names** names the rest, or renames one, and is kept on every install.
- **Names for the app codes Copilot Chat records** (BizChat, Office, OfficeCopilotSearchAnswer, Edge), on the pages that
  showed the codes: Leadership › Apps and agents, Working patterns, Compare two teams and the Person record.

### Changed
- Chargeback groups by *Employee type*, not `EmployeeType`. Suite prices are labeled by the suite's name alone, without
  its part-number pattern.

## [2.7.0] - 2026-10-20

An adversarial review of 2.6.7 and 2.6.8, and its fixes; usability from three people's walk through the dashboard; and
four more of Purview's records with the same app registration: administrators' actions on agents, Teams meeting AI
notes, data loss prevention matches where asked for, and Copilot by product.

To upgrade from 2.6.9: with the nightly task disabled, replace the job server's scripts, run
`Install-CopilotAudit.ps1 -SqlServer <server> -Dashboard` (with `-ActivityDatabase <name>` where there is one) once,
then install the dashboard package over the one in place and enable the task again. The installer adds two tables to
the Copilot database (`audit.ActivityUserDay`, `audit.DlpDay`) and one to the activity database (`audit.DlpMatch`);
nothing existing changes. From the next night, the license-event load also asks for the seven agent-management
operations, and the activity load for `TeamCopilotInteraction`: both from that night on. To load data loss prevention
matches, add `ComplianceDLPSharePoint` and `ComplianceDLPExchange` (and `DLPEndpoint` for devices) to
`ActivityRecordTypes` in the settings, beside the four defaults.

### Fixed (the review of 2.6.7 and 2.6.8)
- **Month-end snapshots showed the whole bank to every leader** on Leadership › Copilot reservations; they are now the
  viewer's organizations only, as a snapshot opened shows them. **Licensing › Reservation holders** is the viewer's part
  of the organization too: a Licensing holder of part of it listed every organization's people.
- **A changed reservation was counted twice**: a new row for an organization left its old one open, and the two were
  summed. A new row now ends the organization's open row the day before it starts; *End* ends a row on a day; a row that
  would overlap one starting on or after it is refused.
- **A pasted register was not all or nothing**: a bad line after good ones left the good ones added and the screen and
  audit log saying *Not changed*. Every line is now checked first (a quoted field may hold commas; a first line that is
  a row is not taken for the header), and the lines are added in one transaction. A database error in any admin
  change is now audited and said.
- **The month-end snapshot** waited for nothing: it is now taken once the licenses reach two days past the month end (a
  license change can reach Purview a day late), only when a reservation is in effect that day (a register entered ahead
  kept snapshots of the pools alone, for good), in one transaction, and not after a load the delete guard refused.
- **The reconciliation and purchase gate counted Copilot Studio's licenses and suites with Copilot** as add-on seats;
  they count the add-on only, as Costs and savings does.
- A leader who has left or whose account is disabled is now an exception (*Leader not in the directory*): the history
  of every user kept them as present. Removing a reservation or a rate is audited with what it was; a rate replaced says
  what it was before. Privacy mode names no leader. Obligations round half away from zero, so rows add up to the total;
  a rate shows as entered. Licenses on disabled accounts counts only those an organization counts.
- **The review of 2.7.0's own additions, before release:**
  - A new reservation row with an end date, inside a row that runs past it, is refused. Adding it ended the longer row
    the day before and left the organization with no reservation once the short one ended.
  - A rate takes at most two decimals, as it is kept.
  - *End on* has a field of its own and its own tip.
  - *Makers* on Build and publish no longer counts administrators who blocked or deployed an agent.
  - Who did an administrator's action is named only with the whole organization in view, since the panel is the organization's.
  - A field's tip is its page's where the same field name is used on several pages: App categories, digests' scope
    and the contracts' start had another page's text.
  - Copilot in Edge counts AppHost *Bing*, which is how Microsoft records Copilot in the Edge browser.

### Added
- **An information icon on every field of every form**: a setting's own description (without its release number), or
  a tip written for the field: the price list's volume price, Dataverse capacity, spare share and *bought for capacity*,
  the reservation register's fields, the resource map, targets, contracts, mail and alerts.
- **Agents › Build and publish › What administrators did to agents**: agents blocked, unblocked, deployed to people,
  removed, deleted or changed in the Microsoft 365 admin center, and the tenant's agent settings changed
  (`CopilotAgentManagement`), loaded with the license events in the same searches. Who did it: for the Agents role.
- **Teams meeting AI notes** (`TeamCopilotInteraction`), loaded by the activity database: the people using it beside
  Power BI and Power Apps on **Apps**, for any part of the organization (`audit.ActivityUserDay`).
- **Data loss prevention matches**, where the activity database is asked for the DLP record types: on **Compliance ›
  Sensitive content**, matches by workload over time and by policy, with those undone: overridden, no longer matching,
  or reported as a false positive (`audit.DlpMatch`, `audit.DlpDay`). Counts only: no file or message is named. Only a
  record that matched a policy is kept: a device's `DLPEndpoint` records also carry its ordinary file activity, audited
  with no rule matched, and those are left out; an endpoint's match is read from its `PolicyMatchInfo`.
- **Governance › Models and controls › Copilot by product**: Microsoft 365 Copilot, Copilot Studio's and other agents,
  Security Copilot, Copilot in Fabric and Power BI, and Copilot in Edge and Bing, from the app each interaction names.
- **How figures are calculated › Copilot's cost a month, five ways**: what each of the five pages' Copilot cost counts,
  and which to quote for what.
- **Admin › Health › Next certificate to expire**, at the top.
- **Governance › AI apps beyond Copilot**: the Foundry accounts billed in the period with no call recorded.

### Changed
- Wording that ties figures counted different ways: *Unused for 30 days* (no use at all) against *Revoke* (no licensed
  use); the two train lists; *Revoke and reassign* (no new cost) against the Upgrade list (new licenses); billable
  against bought on Copilot reservations; the purchase gate against the spare seats. Plain words for the reservation
  figures. **Credit use** leads with what the admin center billed. A suite move to train shows what is at stake, apart
  from the savings. Admin labels: the Copilot price *after discount*, the pay-as-you-go cost with its currency, the
  Fabric discount *as a fraction*, *Idle after* for the Power Platform and AI accounts. Health no longer says no report
  reads the GitHub and Foundry tables.

### Known limits
- **Not run against the client's tenant.** The agent-management records' agent fields (`AgentId`, `AgentName`), Teams
  AI notes' and the DLP records' fields are read as Microsoft documents them; an endpoint match's `PolicyMatchInfo` is
  not yet seen in real records. Each record is kept whole: correct the shred and read them again.
- **The DLP record types are not loaded by default**: their volume is the tenant's every match. The record type for
  DLP of Microsoft 365 Copilot prompts is not documented yet.
- **Copilot by product groups by the app named**, not by each record's app identity, which bi does not keep: a product
  whose records name an app this does not know is counted as Microsoft 365 Copilot.
- The usability pass's remaining items are not in this release: product names in place of SKU part numbers, one
  renewal figure for Budget and renewal and License optimization, and the License inventory's bridge from bought to held.

## [2.6.9] - 2026-10-19

AI apps beyond Copilot, Power BI and Power Apps from the audit log, one place for every assumption, and savings that are
real. An optional **activity database** loads three more Purview record types with the same app registration and no new
permission: calls to Azure AI Foundry and Azure OpenAI from the organization's own apps (`ConnectedAIAppInteraction`),
Power BI and Power Apps activity. **Governance › AI apps beyond Copilot** shows the first; the other two fill who used
Power BI and Power Apps for Costs and savings and Suite fit, which needed a CSV before. **Leadership › How figures are
calculated** lists every price and assumption a figure rests on and where it is set, and every money figure says how
it is worked out, with *Change it* for administrators. Seat-cut savings now count the volume price, the Dataverse
capacity the seats bring, and the seats kept spare on purpose.

To upgrade from 2.6.8: with the nightly task disabled, replace the job server's scripts, run
`Install-CopilotAudit.ps1 -SqlServer <server> -Dashboard` once, then install the dashboard package over the one in
place and enable the task again. The installer adds two tables, a function and a procedure to the Copilot database
(`audit.AIAppUserDay`, `audit.AIAppResource`, `audit.fnAIAppResourceGuess`, `audit.usp_RollupActivity`), six columns to
`dash.CostPrice` (volume price, Dataverse capacity a seat, spare share, bought for capacity; Power Apps Premium seeded at
12 from 2,000 seats) and two settings (`SpareSeatPercent`, 10; `PaygInteractionCost`, 0.12, which was a default in code);
nothing existing changes. Then, on Admin › Targets and assumptions, mark the licenses bought for capacity or the volume
price, and check the spare share. For the activity database, see *Added*.

### Added
- **The activity database** (optional; `docs/OPERATIONS.md`, *AI apps, Power BI and Power Apps*):
  - `Invoke-CopilotAuditBackfill.ps1 -RecordTypes` loads whole Purview record types; a database records them as
    `rt:<type>`, so it is never mixed with operations. Record-type searches are submitted to Graph's beta endpoint, whose
    list of record types has the newer ones.
  - `Install-CopilotAudit.ps1 -ActivityDatabase CopilotActivity` creates and installs it (`audit.AIAppCall`,
    `audit.PlatformActivity`, `audit.usp_ShredActivity`); it must be on the Copilot database's instance.
    `tools\Install-CopilotJobServer.ps1 -ActivityDatabase` saves it in the settings.
  - `Invoke-CopilotPipeline.ps1 -ActivityHistory` loads its history alone (180 days), finalized, shredded and rolled up;
    Copilot interactions are not loaded again. The nightly run then loads it after the Copilot load and rolls it up;
    both steps are optional and fail nothing else. `ActivityRecordTypes` in the settings replaces the three types.
  - `tools\Update-ActivityRollup.ps1` (`audit.usp_RollupActivity`) copies what the dashboard shows into the Copilot
    database, cutting days in the reporting time zone: AI app calls by day, resource, app, subscription and caller; the
    resources seen; and Power BI and Power Apps last active and active days in 30 (`audit.PowerPlatformUserActivity`,
    each product's rows replaced only when the activity database holds records of it).
- **Governance › AI apps beyond Copilot**: calls, conversations, resources, the share of calls without a user identity
  (an app calling with its own key), people (counted from the minimum group size, never named) and failures; calls over
  time with and without a person's identity; by line of business and environment; each resource with its Azure cost
  where the Foundry loader runs; departments calling with their own identity; and, for administrators, the resource
  map (line of business, environment and owner, guessed from names like `retail-prod-aif-0147` until set).
- **Leadership › How figures are calculated**: every price and assumption (49 on the demo) with its value, the
  figures that use it, who last changed it or *As installed*, and *Change it* for an administrator who may set it,
  which opens the page at the field; prices that disagree (the Copilot price in Access against the reservation rate
  in force and the contracts' price); and how each kind of figure is worked out.
- **How it is worked out**, under every money figure: on Costs and savings' tiles, Chargeback, License optimization,
  Budget and renewal, Rollout waves, License action outcomes, Fading use, Better with a license, Better on
  pay-as-you-go, Swap list, Credit use (the credit formula with its rates, and the token formula), Agents costing
  credits, Suite fit, Value and time saved and Copilot reservations: the formula with its values, then each assumption
  with its value and, for an administrator, *Change it*.
- **Prices that disagree** are said at the top of Costs and savings.

### Changed
- **Seat cuts are counted at what they really save** (Costs and savings):
  - **Spare seats kept on purpose** are not a saving: *Spare seats kept unassigned* (10% of those bought) for the
    Copilot add-on and every license, and a license's own share on its price row. 754 unassigned Copilot seats of
    18,640 are within the spare; nothing to cut.
  - **Bought for capacity or the volume price**: a price row's switch; its unassigned and idle seats are never money
    back (Power Apps Premium bought to 2,000 seats for its Dataverse capacity).
  - **The volume price**: the spend counts it, and a cut under it counts every seat going back to the list price, or
    stops at it where crossing it would save less.
  - **The Dataverse capacity a seat brings**: a cut that gives up more than the tenant has spare is less the add-on
    capacity it would need; without the capacity loaded, the saving is Low confidence and says it was not checked.
  - The monthly spend trend counts the volume price too.
- Chargeback's *Bought, not assigned* says what is beyond the seats kept spare.
- The installed settings include the pay-as-you-go cost of an interaction (0.12), which the pages assumed when unset.

### Fixed
- `Install-CopilotAudit.ps1 -ReportingTimeZone 'Eastern Standard Time'` on new, empty databases recorded nothing, as Eastern is the
  zone a database with none reads; the next install with no zone named then gave them the server's own zone (UTC on a
  server set to UTC). The zone named is now always recorded.

### Known limits
- **Not run against the client's tenant.** The three record types' fields are read as Microsoft documents them and as
  the client's two sample records showed (`AppIdentity`, `CopilotEventData.Contexts`, `Messages`, `ThreadId`,
  `AccessedResources`; Power BI's `ItemName` and `WorkSpaceName`; Power Apps' `AdditionalInfo`); the application name
  and subscription are read from three likely places each. Where one is named differently, correct the shred and read
  every record again (`EXEC audit.usp_ShredActivity;`): the records are kept whole. Submitting record-type searches to
  beta is untested against the live service; the stand-in accepts both endpoints.
- **Calls without a user identity cannot be tied to a person**, only to a resource and subscription.
- **The Dataverse check needs the capacity loaded** (`PowerPlatformCapacityDay`, by CSV until the Power Platform
  permission arrives). Fabric capacity, Copilot Credits and flow runs still come from CSV exports.
- Line of business and environment are guessed from resource names until an administrator sets them.

## [2.6.8] - 2026-10-19

Microsoft 365 Copilot reservations and chargeback: **Leadership › Copilot reservations** sets each line of business's
reserved seats against the add-on licenses its people hold, and shows what that bills, as the client's reservation
policy defines it: reserved, allocated, unused, over, billable, utilization and the monthly and remaining obligation by
organization, the reconciliation with the license pool, the purchase gate, exceptions to clear, and month-end snapshots
kept as they were. It is the current operational state, never certified; certification, adjustments, OIM intent and
the December close follow in later releases.

To upgrade from 2.6.7: with the nightly task disabled, replace the job server's scripts (the pipeline now keeps the
month-end snapshot), run `Install-CopilotAudit.ps1 -SqlServer <server> -Dashboard` once, then install the dashboard
package over the one in place and enable the task again. The installer adds four tables (`dash.Reservation`,
`dash.ReservationRate`, `dash.TechnologyAllocation`, `dash.ReservationSnapshot`), the function
`dash.fnReservationMember`, the procedure `dash.usp_TakeReservationSnapshot` and two settings; nothing existing
changes. Then, on Leadership › Copilot reservations, an administrator pastes the reservation register (Org ID,
organization, leader's sign-in, reserved, from), adds the rate (18 a license a month for 30 less 40%), sets the
purchase-gate baseline, and approves the Technology-funded accounts. The first snapshot is kept the next night.

### Added
- **Leadership › Copilot reservations.** For leaders, each seeing the organizations led from inside their part of the
  organization; the whole-organization view and the Licensing role see all.
  - A state line first: *Current operational state, not certified*, the day of the licenses read, and that billable
    licenses follow the policy's rule pending Finance's confirmation; or, for a snapshot, *Operational, not certified*
    and the day it was kept.
  - Figures: reserved; Entra allocated (the Microsoft 365 Copilot add-on); unused reserved capacity (the sum of
    MAX(reserved − allocated, 0) by organization); allocation over reservation (the sum of MAX(allocated − reserved,
    0)); billable (reserved plus allocation over reservation); the monthly obligation (billable times the rate in force)
    and the remaining obligation (the months left in the year); add-on holders under no listed leader; the
    Technology-funded pool. Without a rate the obligations read *Pending Finance Decision*.
  - One row for each organization: Org ID, leader, cost center, reserved, allocated, unused, over, billable,
    utilization (N/A with nothing reserved), status (*Within*, *At* or *Over reservation*, *No reservation*, *Data
    exception*) and its monthly obligation, then those under no listed leader, the Technology pool and the total.
  - **Reconciliation and purchase gate**, for the whole organization: the add-on licenses bought, assigned and
    unassigned; the holders as counted here, and the difference to reconcile; the unused reserved capacity beside the
    unassigned inventory; and the purchase gate (no purchase until 95% of the baseline's unassigned licenses are
    assigned: at 1,136, 1,079, leaving a buffer of 57).
  - **Exceptions**: leaders not in the directory, a leader on two organizations, holders under no listed leader,
    non-human-looking accounts not in the Technology register, Technology allocations past their review date, licenses
    on disabled or departed accounts, and no rate in force; each with what to do and where.
  - **Month-end snapshots**, each kept once by the nightly run and never rewritten, with *View* to show one as it was.
  - For administrators: the **reservation register** (one at a time, or pasted from a spreadsheet, all checked before
    any is added), the **rate** by effective date, the **purchase gate**, and the **Technology-funded register**
    (account, category, accountable owner, approval reference, from and review dates). Every change is audited.
- **Licensing › Reservation holders**: the people an organization's allocated licenses count, those under no listed
  leader, or the Technology pool, with manager, licensed since and last use. For the Licensing role.
- `dash.usp_TakeReservationSnapshot`, run by the nightly pipeline after the licenses are read (a failure is logged and
  fails nothing), and `dash.fnReservationMember`.

### Known limits
- **Who counts where is a default**: an organization's people are everyone under its leader in the org chart, and a
  person under two listed leaders counts with the nearer one. A persistent chargeback hierarchy from another source, and
  cost-center mapping beyond the register's column, are not read.
- **Billable follows the policy's MAX(reserved, allocated) rule**, labeled pending Finance confirmation: the
  reserved-capacity proposal would bill on reservations alone. The purchase gate counts unassigned inventory; which pool
  it should count is Finance's to decide, and the unused reserved capacity is shown beside it.
- **Not yet built**: OIM approved intent, certification and sign-off, adjustments with approval lineage, recovery and
  overlap, true-down credits and new-reservation charges, the December close and the January chargeback output, and
  the masking of named users by role. Snapshots use the org chart of the night they were kept.
- **Tested on the demo database** (5,000 people, 15 reservations) and the SQL integration suite, not on the client's
  register or tenant.
- **A full bi rebuild can still take minutes on one month**, as in 2.6.6 and 2.6.7: its fix follows in its own release.

## [2.6.7] - 2026-10-19

A headline figure now opens a page that shows the same number, or has no link: a check that followed every linked
figure on the 77 report pages, for the whole organization and for a team, found figures opening pages that counted
another way. *Unused for 30 days* is counted one way on the three pages that show it, and Engagement's *Need a nudge*
opens the people it counts.

To upgrade from 2.6.6: install the dashboard package over the one in place. Nothing in the database changes; the job
server's scripts are as 2.6.6 had them.

### Fixed
- **Engagement's *Need a nudge* opened the power users**: People by engagement shows one segment, Power unless one is
  chosen, and the figure chose none. It now opens *Need a nudge* there, the one-off and not-started people together with
  the protected levels left out, as the figure counts them (692 in the demo: 168 and 524).
- ***Unused for 30 days* counts as Unused licenses lists it**, on Licensing and on Forecasting › License optimization:
  someone licensed fewer than the inactive days ago is not yet a candidate to reclaim (524 against a list of 515 in the
  demo). On License optimization the figure, *Who they are* and the reassign card open that list, and the department
  table counts the same way.
- **Licensing's *Licenses in use* counts every idle license**: those held by the protected levels counted as in use.
  It now has no link: it is a share, not a list.
- **Leadership's *Active in the last 7 days*** was described as a share of Premium license holders; it counts everyone
  who used Copilot, licensed or not, and now says so.

### Changed
- **Headline figures open the page that shows them**:
  - Licenses (Board pack), Premium license holders (Scorecard) and Licenses held (License optimization) open Licensing,
    which shows the licenses held today; License pool counts the Copilot add-on's seats, which is fewer.
  - Adoption on Leadership, the Board pack and the Scorecard opens Adoption. Usage by license counts enabled accounts
    only (80.6% for 80.4% in the demo).
  - People using Copilot opens Use ranking, for those with the Licensing role; for anyone else it has no link.
  - Chargeback's Licenses held opens Usage by license, which shows the same number.
  - On the agent lists, *Not used in the period* opens the same list showing only those.
- **Figures counted another way than any list have no link**: Chargeback's unused spend (the period's, by its own
  rule), and the agent lists' shared and in production (counted within the page's own list and filters).

### Known limits
- **The links are checked against the demo**, at whole organization and for one team, signed in with every role and
  with the Licensing role taken away; the unit tests check the links in the code, not the numbers. Four figures open
  what they are derived from rather than the same number: Have started (a share) its complement, credits billed their
  cost, and interactions their breakdown or the people behind them.
- **A full bi rebuild can still take minutes on one month**, as in 2.6.6: the fix follows in its own release.

## [2.6.6] - 2026-10-19

One page for the whole AI and automation bill: **Leadership › Costs and savings** shows what Microsoft 365 Copilot,
Copilot Studio's credits, Fabric with Copilot in Power BI, Power BI, Power Automate, Power Apps with Dataverse, GitHub
Copilot and Foundry cost a month, what of it can be saved, and what it returns, with the savings ranked by what they
are worth and each figure leading to the people or resources behind it. Two Licensing pages go with it:
**Suite fit**, for E3, E5 and E7, where a saving is a move down or training rather than a license taken back, and
**Credit use**, an estimate of Copilot Credits and tokens by person, which the agent credit pages now share. Six new
tables hold the Fabric and Power Platform sources, loaded from their own exports; the demo company grows to 5,000
people, with its agents, guests, service accounts and a realistic six months of that spend.

To upgrade from 2.6.5: with the nightly task disabled, replace the job server's scripts, run
`Install-CopilotAudit.ps1 -SqlServer <server> -Dashboard` once, then install the dashboard package over the one in
place and enable the task again. The installer adds six tables (`audit.FabricCapacityDay`, `audit.FabricWorkloadDay`,
`audit.PowerAutomateFlow`, `audit.PowerPlatformUserActivity`, `audit.PowerPlatformCapacityDay`,
`audit.PowerPlatformCreditDay`), the price list `dash.CostPrice`, the suite list `dash.CostSuite`, the column
`audit.LicenseSku.CopilotInSuite`, the views `dash.CopilotSuiteOnly` and `dash.CopilotSuiteHolder`, and ten settings;
nothing existing changes and nothing is rebuilt. The next license lookup marks the suites with Copilot inside. The page
shows Microsoft 365 Copilot, and GitHub Copilot and Foundry where their loaders run, from the first visit. Load the
Fabric and Power Platform exports with `tools\Import-CostCsv.ps1` to add their areas, and put your agreement's prices,
suites and credit rates in Admin › Targets and assumptions.
### Added
- **Leadership › Costs and savings.** For whole-organization viewers and the Licensing role.
  - Laid out as a dashboard, the most important first and the cards two to a row, so it reads in about two screens;
    from 390 pixels wide to 1920, in the light theme and the dark.
  - *The headline:* one sentence (the spend, its return, and what taking the savings would do), then five tiles: the
    spend a month and a year with its last six months, the savings found with their share of the spend, the value
    delivered (time saved at the hourly cost, in hours and in people's working time), the return on the spend as a
    dial marked where it would be once the savings are taken, and what this dashboard's license exports have saved.
  - *Savings to take:* every saving the rules find, ranked by what it saves a month: the three largest with their
    evidence and what to do, the next seven as rows with theirs behind an information icon, any more behind a
    button, each linked to the page with the detail where there is one; and a CSV of them all. Beside them,
    *Savings by confidence* (High: the data shows it; Medium: it follows from a change you make; Low: it rests on a
    price or a discount) and *Where the money goes* (a ring of the areas' shares).
  - *Every area, side by side:* each area's spend, a bar whose hatched end is the part that can be saved, its last
    six months, the savings, the value and the return; how each was worked out behind its information icon.
  - *Grow the return* (people to train, heavy free users to license, failing flows, throttled capacity) beside
    *Copilot in Power BI* (its return, share of the capacity, cost, requests and cost a request); then *Spend by
    month* for the last six whole months beside *What this is built from* (the sources loaded and not).
  - The rules: Copilot licenses unused, on disabled accounts, or better on pay-as-you-go (the same rules as the
    Licensing pages, protected levels respected); Copilot Credits past the packs that would cost less as packs, and
    packs unused; Fabric capacities idle, oversized (peaks under 45%), better reserved or paused at weekends, and
    throttled; Power BI, Power Automate and Power Apps licenses unused in 30 days (a Power Automate holder who owns a
    premium flow that ran counts as using it); Process licenses on flows that run under 100 times a month; Dataverse
    capacity over the entitlement; GitHub Copilot seats idle; a Foundry account that cost money and answered nothing.
  - Every figure a month: licenses held now at their price, and each usage source's last 30 days. A cached load
    takes about 30 ms on the 5,000-person demo; the page has no team filter, as the spend is the tenant's.
- **Six tables for the new sources** (`Create-CopilotExternalUsageSchema.sql`), each filled from its source's own
  export, no API permission needed: Fabric capacity days (the Capacity Metrics app, with the capacity's cost), Fabric
  workload days (Copilot in Power BI is a workload of its own), the cloud flow inventory, who used Power BI, Power
  Apps, Power Automate and Copilot Studio, Dataverse capacity, and Copilot Credits.
- **`tools\Import-CostCsv.ps1`** loads a CSV into any of them, by the table's own column names: adds and replaces
  by each row's key, reads dates and numbers the same whatever the machine's language, leaves out and counts a row
  whose key cannot be read, keeps the last of a repeated key, names headings it has no column for, and with
  `-Replace` makes the file the whole table, held by the delete guard (`CostImportMaxDropPercent`).
- **Prices and assumptions** in Admin › Targets and assumptions › *Costs and savings*: the price of each license
  and unit (`dash.CostPrice`, seeded with list prices in USD), the Fabric reservation discount (41%), the minutes a
  flow run and a Copilot in Power BI request save (3), and the days after which a seat, license or AI account is
  idle (30).
- **The demo company has 5,000 people** (`New-InsightsDemo.ps1 -People`, 400 to 5,200; 2,317 before), and
  `New-InsightsDemoCosts.sql` gives it six months of spend for every rule to find: three Fabric capacities (a
  reserved production F128 throttled at month end with Copilot in Power BI growing on it, an oversized development
  F64 on pay-as-you-go idle at weekends, an idle sandbox), Power Platform licenses with a share unused, 921 flows,
  Dataverse over its database and log entitlement, Copilot Credits outgrowing their packs, 384 GitHub Copilot seats
  with a fifth idle, and a provisioned Foundry deployment nobody has called in five weeks. Names stay unique, and the
  first 2,317 people are who they were.

- **Suites: E3, E5 and E7.** Saving on a license is seldom revoking it once suites are in play, so the dashboard now
  knows them (`dash.CostSuite`: Microsoft 365 and Office 365 E3, E5 and E7, their price, and what each includes).
  - *Licensing › Suite fit*, a new page: each holder with the suite that would serve them, from their Copilot and
    Power BI use. E7 with no Copilot use to E5 (Medium); with light use, train first; with regular use, E5 and the
    add-on where cheaper (Low). E5 with no Power BI use to E3, with it to E3 and Power BI Pro where cheaper (Low,
    and only with security, compliance and telephony's agreement). Copies held beside a suite to drop (High), and
    suites on disabled accounts (Medium). Each move names what the data cannot see and must be checked first.
  - *Costs and savings*: none of a suite's price is counted as AI spend, nor Copilot or Power BI Pro inside it;
    a standalone one on a suite's holder is a saving (High); seats bought and assigned to nobody are a saving
    (Medium), the spend being the seats bought; and a *Suites* card shows what the suites include and the moves to
    review, outside the AI savings.
  - Copilot inside E7 licenses its holders on every page (the license lookup's `-CopilotSuiteSku`), is never on a
    list to revoke (`dash.CopilotSuiteOnly`), and a move from the add-on to E7 keeps them licensed through it.
  - Admin › Targets and assumptions › *Suites*: each suite's price, what it includes, and on or off.

- **Licensing › Credit use**: Copilot Credits, and tokens, by person, estimated from the audit log, since Microsoft
  reports neither per person. Every agent (Copilot Studio, Copilot Studio lite, SharePoint) and every use that bills:
  an agent used without a license, or in a Copilot Studio channel; more where it read work data or called another
  agent. For a credit policy: people by credits a month, what a cap a person would touch, the departments and agents
  that bill most, and the unlicensed people costing more than a license. Tokens are a rough figure for sizing. The
  rates are Admin › Targets and assumptions › *Credit estimate*'s (Microsoft's list rates by default).

- **From the headline to the detail.** On Costs and savings each tile, area, confidence, saving and suite move
  leads to what it is made of: each area has its own view (`?area=`), with every saving and risk in it, its six
  months, a table of what it is made of (capacities, licenses bought and assigned, flows, accounts, seats) and the
  reports that name the people; a confidence lists its savings alone. A report's headline figure can now link to
  its detail (`Kpi.Href`), and Leadership's overview, What to act on and the section start pages do.
  A link is shown only where the viewer may open its page (`InsightsPage.Open`, the menu's own test): a leader
  without the Licensing role sees the figures, and no link to the lists that name people.
  Leadership › What to act on lists the savings found and the suite moves to review, for those who may see them.
- **Suite seats bought and assigned to nobody** are a move to review (E3, E5 and E7), and Licensing › Credit use
  shows the credits the admin center billed beside its estimate.

### Changed
- **Every report's heading is shorter**: the title and who the page is for on one line, what it helps with and what
  to do on the next, and less space around them and the filter bar, so the figures start about 100 pixels higher. *Helps you* and *Do* stay whole, side by side where they fit.
- **A chart of six points draws all six labels**: the one before the last was left out wherever every point had
  room for its own (every month chart, every short trend).
- **The license pool's Copilot seats are the add-on's**: a suite with Copilot inside (E7) is shown as such and is
  never counted, assigned or removed as the add-on, by any page or export.
- **Copilot held only inside a suite stays off the add-on's lists**: the License pool's *Used in the last 30 days*,
  *Licenses on disabled accounts* and the train list leave out E7 holders without the add-on. Suite fit reclaims a
  leaver's suite and lists the E7 holders to train.
- **Links from Costs and savings open the whole organization**, as the page itself counts it, and only to pages the
  viewer may open. Unassigned suite seats, and Credit use's comparison with what the admin center billed, are shown
  only where the whole organization is: both are the tenant's.
- **Agents costing credits and Better with a license use Credit use's estimate**: credits an answer, more where it read
  work data or called another agent, at the rates in Admin › Targets and assumptions, instead of a flat price an
  interaction, so the three pages give one figure. An unknown license status is no longer taken for none, and Copilot
  Chat without an agent, which is free, is no longer counted as metered. Agents costing credits shows credits and their
  cost apart. Better on pay-as-you-go still prices a licensed person's use an interaction at a time.
- **Build and publish names every agent's owner the agents pages know**: the agent inventory's, else the one set in
  Admin, where it read only Admin's.
- **A license held twice is counted once, the same way, on both pages**: an add-on beside the suite that covers it,
  for enabled people outside the protected levels, the Copilot add-on only when the revoke list does not take it already.
- **The demo keeps levels 1 to 3 off the revoke and train lists**, not 1 to 5: with five levels, the default kept
  the whole demo company off them and left those pages empty. A real install's setting is unchanged.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.
- **The new sources are loaded from exports, by hand or on a schedule you set**: no loader calls the Fabric or Power
  Platform APIs yet. Each export has to be shaped to the table's headings (OPERATIONS.md lists them).
- **Prices are list prices in USD** until set; licenses with part numbers not in the price list are left out of the
  spend. Check `audit.LicenseSku` for your tenant's part numbers.
- **Value is counted only where it is measured** (Copilot, flows, Copilot in Power BI), at minutes per use that are
  assumptions; GitHub Copilot, Foundry and the licenses without usage show no value, so the return errs low.
- **Tested on the demo database and the SQL integration suite only**, not against a real tenant's exports.
- **The credit estimate is only as good as its rates**: credits an answer, for grounding and for an agent action are
  set in Admin, not read from Microsoft, and a classic, scripted answer costs less than counted. Where the admin
  center's consumption export is loaded, Credit use shows the two side by side: bring the rates near it.
- **A full bi rebuild can take minutes on one month** (`bi.usp_RefreshBi`'s accessed-resource insert, a plan chosen
  on statistics taken while the dimensions were nearly empty): seen on the demo, 12 minutes instead of under one. A
  nightly refresh reads a few days. The fix follows in its own release.
- **Not tried in Power BI Desktop**: the Power BI model is unchanged (`generate_model.py` writes it as 2.6.5 had it), and was not refreshed against the demo.

## [2.6.5] - 2026-10-14

A delete guard on every load that empties a table and fills it again, each with its own percentage; and the release
no longer carries third-party modules or the NuGet package list.

To upgrade from 2.6.4: with the nightly task disabled, replace the job server's scripts and enable the task again.
Nothing in the databases changes, and the dashboard is the same as 2.6.4's (installing its package again is harmless).
**The offline zip is gone**: before replacing the scripts, keep the `Modules` folder beside them, or fill it again from
your own feed (`Microsoft.Graph.Authentication`, `ImportExcel`, and `PnP.PowerShell` for a workbook on SharePoint
Online). From the first night, each guarded load compares with what the table holds, at 15% unless the settings say
otherwise: a night whose source really did shrink by more is refused, and is let through once with `-AllowDrop`.

### Added
- **The delete guard.** Five loads replace a whole table each run. Each now counts what it read against what the
  table holds, before it deletes anything, and refuses a load that is more than its percentage smaller: 10,000 rows
  last night and 8,000 tonight is 20% fewer, refused at 15. The table keeps the last good load, so a source that
  answered with part of its rows (an AD query cut short, a workbook saved half-filled, an API that returned one page)
  can no longer empty the reports. A first load, a load that grows or stays level, and a drop of exactly the
  percentage go ahead.

  | Load | What it replaces | Setting |
  |---|---|---|
  | Agent workbook, `tools\Import-AgentWorkbook.ps1` | Each sheet's table, and the agent inventory read from them. Every sheet is checked before any is replaced, and the whole workbook is refused if one fails. | `AgentWorkbookMaxDropPercent` |
  | User directory from AD, `tools\Export-UsersWithHierarchy-ToSql.ps1` | `audit.UserDirectory` | `UserDirectoryMaxDropPercent` |
  | Licenses from Graph, `tools\Update-CopilotAuditEnrichment.ps1` | `audit.UserLicenseSku`. Nothing about the people is written when it is refused; the license pool still is. | `LicenseMaxDropPercent` |
  | GitHub Copilot, `tools\Import-GitHubCopilotUsage.ps1` | `audit.GitHubCopilotSeat`, for the scope. Usage and cost still load; the run ends *Partial*. | `GitHubSeatMaxDropPercent` |
  | Foundry, `tools\Import-FoundryUsage.ps1` | `audit.FoundryDeployment`, per subscription. Usage and cost still load; the run ends *Partial*. | `FoundryDeploymentMaxDropPercent` |

  - **Each percentage is its own**, 15 by default, set in `copilot-audit.settings.psd1` as a number from 0 to 100; 0
    turns that guard off. The nightly run passes each loader its own. Each loader also takes `-MaxDropPercent` for one
    run, and `-AllowDrop` to let a real drop through once.
  - **A refusal does not stop the night.** The workbook, directory and license loaders end with exit code 3 when
    refused. The nightly run records it on the step, says in its log that the table kept its last good load, and goes
    on: the license events and interactions still load. The run ends *Succeeded*, and its detail on Admin › Health ›
    *Pipeline runs* names each refused step with the guard's reason. Any other failure still stops the run.
  - **The reason names** the table, both counts, the drop, the limit and the setting. The workbook's refusal is also
    on its own Health panel, as a failed run; GitHub's and Foundry's in their runs' detail.
  - The other loads only add or update rows, or rebuild from what is already in the database, and the dashboard's
    uploads only add, so none of them needs a guard.
  - `tools\DropGuard.ps1` holds the rule; `tests\Test-DropGuard.ps1` tests it, and the SQL integration test runs the
    workbook, directory, license and seat guards against a database.
  - `tests\Test-Workflows.ps1` now also fails on a workflow step joined to the line before it, and on a blank line
    inside a command continued with `\`: GitHub reports either only as a workflow that never ran.

### Fixed
- **A workbook sheet with one column failed the agent workbook load** ("[System.Char] does not contain a method named
  'Replace'"): PowerShell gives a single column's name as a string, not a list, and the loader took its first letter
  as the column. A sheet such as `AgentsInProd` with only `AgentId` now loads. Found by the 2.6.5 tests.

### Removed
- **The offline zip of bundled PowerShell modules** (`copilot-audit-backfill-vX.Y.Z-offline-win-x64.zip`, with
  `Microsoft.Graph.Authentication` and `ImportExcel`). The release has the source zip and the dashboard package, each
  with its checksum, and the two reporting guides. The modules come from the PowerShell Gallery or your own feed;
  `tools\Save-CopilotAuditModules.ps1` still saves them into a `Modules` folder beside the scripts, at the tested
  versions, signatures and hashes checked, and the scripts still look there first.
- **The NuGet package list** in `docs/BUILD-FROM-SOURCE.md`, with the test that kept it, and the guide's section on
  building from a copied folder of packages. A build restores from nuget.org or your own feed; the guide says how to
  name a feed. The prebuilt dashboard package is unchanged.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- **The guard has no minimum.** A table of a few rows trips it with one removal (4 Foundry deployments to 3 is 25%).
  Raise that loader's setting, or set it to 0, where small numbers are normal.
- **A refused license run still writes the license pool**, which is replaced whole too but holds one row a SKU; it
  is not guarded.
- **Not tested against a real directory, workbook or API.** The guards ran against a database with stand-ins for
  Excel, Graph and the GitHub writer, and the nightly run's handling of exit code 3 was tested on its own step
  runner, not in a whole night.

## [2.6.4] - 2026-10-13

An administrator can be limited to some of the fourteen Admin pages; Agents › Agent inventory filters on where an
agent was made; and a new guide to compiling the dashboard yourself, with the NuGet packages a build needs.

This is not the 2.6.4 published on 2026-10-10 and withdrawn the next day. That build gave chosen Admin pages through a
role of its own, Persona; this one limits the Admin role itself. A database that build was installed over is carried
across by the installer (see *To upgrade*).

To upgrade from 2.6.3: with the nightly task disabled, replace the job server's scripts, run
`Install-CopilotAudit.ps1 -SqlServer <server> -Dashboard` once, then install the dashboard package over the one in
place and enable the task again. The installer adds one column, `dash.RoleGrant.Pages`, which takes NULL. Nothing is
rebuilt and no figure changes. Every Admin grant in place stays a full administrator. Where the withdrawn build left
Persona grants, each becomes an Admin grant limited to the same pages, unless the same person or group already holds
Admin, which stands. A 2.6.4 dashboard over a database not yet upgraded works: every Admin grant is whole, and no
pages are offered to tick.

### Added
- **Admin › Access: an administrator for some Admin pages only.** In *Grant a role*, with Admin ticked, tick under
  *Admin pages* any of the fourteen: Health, Access, Directory quality, Email, Alerts and digests, Targets and
  assumptions, Privacy and retention, Reference data, Value names, Classifications and events, Reports shown,
  Scheduled extracts, Training records, Audit log. That person or group sees those pages in the navigation and no
  other Admin page, changes what is on them, and is refused the rest: the page, its forms and its files. None
  ticked, or all fourteen, is the whole role, as every Admin grant was.
  - Granting Admin again to someone who holds it replaces their pages, and the message says what the role now covers.
  - The grants list shows a limited Admin role with its pages in brackets; *Check someone's access* says what a
    person's Admin role covers.
  - Only a full administrator gives, limits or removes Admin, so someone limited to Access cannot make themselves,
    or anyone, an administrator. They can grant and remove the other roles. Nobody can limit their own Admin role.
  - Someone in several grants gets the pages of all of them; one whole Admin grant among them makes them a full
    administrator. Administrators named in `Insights:BootstrapAdmins` are always full.
  - Outside the dashboard's levels a limited administrator keeps their pages, as a full one keeps all of them. The
    data API's `health` call answers whoever may open Admin › Health. A change on a report page that only an
    administrator may make (a contract on Budget and renewal) stays with full administrators.
  - `dash.RoleGrant.Pages` holds the pages as their paths, comma-separated; NULL is the whole role.
- **Agents › Agent inventory: a *Created in* filter.** A box with the values the workbook's `CreatedIn` column holds
  (*Copilot Studio*, *Copilot Studio Lite*), *All* first. A choice keeps the list, its counts and its CSV to those
  agents. The period and the part of the organization still decide the use shown, and the choice stays on the page's
  own links: the period, the way down the organization, *All* and *Not used*, the sort and the search. It is read
  from `dbo.AgentInventory`, the sheet as the nightly workbook load keeps it; only a value the column holds reaches
  the query, as a parameter. The box appears when a workbook with the column is loaded.
- **Filters on a workbook list are one line each.** A page that lists the workbook's agents names its filters as
  `new("createdin", "Created in", "CreatedIn")`: the name in the address, the box's label, the sheet's column. No
  column is added to a table and the load does not change. Agent inventory has the one; Shared agents and the other
  lists have none.
- **`docs/BUILD-FROM-SOURCE.md`: compiling the dashboard yourself.** What a build needs (the .NET 10 SDK, the
  source, the NuGet packages), the 24 packages by name and version with what brings each in, building and installing
  with internet access, and building with none from a folder of the packages. A test fails when the list is not what
  a restore brings.

### Changed
- **The Admin role's description** on Admin › Access says it can be limited to pages.
- **A page that was refused names full administrators** as the people to ask, not one limited to some pages.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- **The pages of an Admin grant are set on Admin › Access only.** `Invoke-CopilotMaintenance.ps1 -Task GrantRole
  -Role Admin` grants the whole role.
- **The *Created in* filter was tested on the demo database** with a workbook shaped like a client's sample, and not
  against a client's own workbook.
- **Building with no internet access was tested on one PC**, with the package folder as the only source and an
  unreachable proxy standing in for no network, not on a server without a route out.

## [2.6.3] - 2026-10-08

An agent the audit log names two ways is one agent, with all its use and the workbook's name and owner; Admin › Access
removes every role of one person or group, or of several at once; the schema is held to backward compatibility from
this release on; and three new parts of the documentation: the naming conventions, what uses each `bi` table and
view, and every script in summary.

To upgrade from 2.6.2 (or 2.6.0, 2.6.1): with the nightly task disabled, replace the job server's scripts, run
`Install-CopilotAudit.ps1 -SqlServer <server> -Dashboard` once, then install the dashboard package over the one in
place and enable the task again. The installer adds one column, `bi.DimAgent.MergedIntoKey`, and the procedure
`bi.usp_MergeAgents`, and runs it: the interactions kept under an agent's second identity move to the agent, once.
On a large database that first run rewrites those interactions in batches and can take some minutes; every run after
it reads one column and moves nothing. Agents the workbook proves are one are folded in by the next run of the agent
workbook load, the same night. Refresh the Power BI model in full once afterwards, as the installer says when it
moved anything. Nothing else is rebuilt.

### Fixed
- **One agent was counted as two where the audit log names it two ways.** A client's agent reports listed hundreds
  of agents the workbook "did not match", though the workbook held every one of them. Three cases, each now one
  agent with all its interactions under one key:
  - **An agent made in Microsoft 365 Copilot.** Records name it `Copilot.Studio.Default-<tenant>-<its id>` or
    `Copilot.Studio.Declarative.T_<its title id>`. Nothing in a record ties the two, but the agent workbook's row
    holds both ids. `tools\Import-AgentWorkbook.ps1` linked a workbook agent to one identity only, so the other was
    left without the workbook's name, owner and inventory details. It now marks every other identity the row matches
    exactly as another name for the agent. An agent keeps the key it had, so nothing that points at it moves.
  - **A SharePoint agent, and a declarative agent, written with and without its prefix**:
    `SharePointAgents.Declarative.SPO_<id>` and `SPO_<id>`, `Copilot.Studio.Declarative.T_<id>` and `T_<id>`. The
    agent is the one with the prefix. This needs no workbook.
  - **One of Microsoft's own agents under two variants**, such as
    `MicrosoftAgent.Word Drafting Agent.BuiltIn_WordDraftingAgent` and `...WordDraftingAgent`. The name in the id is
    Microsoft's own for the agent, so the same name is the same agent: the one seen first. This needs no workbook.
  Agents of your own that only share a name are never merged: two makers' *New Agent* have different ids and are two
  agents. The workbook's "matched" count does not change: it counts workbook agents the log knows, and they were
  linked already. What changes is the number of agents listed, the use shown for each, and the list of agents in use
  that the workbook does not cover, which is now only the agents it really does not list.

### Added
- **`bi.usp_MergeAgents`** (`Create-CopilotBiProcedure.sql`) and **`bi.DimAgent.MergedIntoKey`**. A row of
  `bi.DimAgent` whose `MergedIntoKey` is set is a second identity of the agent with that key. The procedure sets it
  for the prefix and Microsoft cases, follows a chain to its end, clears a mark that cannot stand, and moves what was
  kept under the second identity to the agent: the interactions (`bi.FactInteraction`), the flagged ones
  (`dash.FlaggedInteraction`), the workbook's links (`dash.AgentInventory`, `dash.AgentChildLink`), a name given by
  hand (`dash.AgentAlias`, where the agent has none), and `dash.AgentUse`. `bi.usp_RefreshBi` runs it at the end of
  every refresh and writes an interaction that arrives under a second identity with the agent's key from the start;
  the workbook load and the installer run it too. Nothing is deleted: the second identity's row stays, as the record
  of the name.
- **`bi.Agents` and `dash.Agent` list an agent once.** Both leave out the rows that are a second identity. A report
  that counts agents should count `bi.Agents`, not `bi.DimAgent`.
- **Admin › Access: remove every role of one person or group.** *Remove all*, at the end of a row's roles, removes
  every role that person or group holds, after asking.
- **Admin › Access: remove several people's roles at once.** A tick box on each row of the grants, *Select all on
  this page*, and *Remove every role from the selected* under the table. Up to 500 at once. Both removals are admin
  changes like any other: refused to anyone but an administrator, and recorded in the audit log with each person or
  group and the roles they held. An administrator who clears their own account is told so. Someone cleared keeps
  what their level and their AD groups give them.
- **The schema is held to backward compatibility.** From this release a release may add to the schema and may not
  take away from it: no table, view, procedure, column, parameter or grant to a role is dropped or renamed, a column
  keeps its type or widens it, a view keeps its columns in their places, and a new column or parameter never has to
  be given. `tests/schema-baseline.tsv` is the shape of the last release, both databases. CI compares what a build
  installs with it and with the baseline the last release's tag carries, then installs 2.6.2 and the last release
  from their tags, installs the build over each, and fails unless the databases end with the schema a new install
  has. `tools/dev/schema/Update-SchemaBaseline.ps1` takes the baseline again at each release, and refuses when the
  release breaks the one in place. This release changes the schema within the rule: one column that takes NULL, and
  one procedure.
- **`docs/SCRIPTS.md`: every script in summary.** What each script does, who runs it and when: setup and upgrade,
  the nightly run and the loaders it calls, the day-to-day tools, each SQL script in the order the installer runs
  them, the dashboard's, Power BI's, the development tools and the tests.
- **`docs/SCHEMA.md`: the naming conventions.** What a schema, a prefix, a suffix and a column's ending say about an
  object: who it is for, what kind it is, and what one row of it is.
- **`docs/SCHEMA.md`: what each `bi` table and view is for, and what uses it.** Every dimension and fact table with
  the view over it and its readers (the dashboard, its rollups, the Power BI model, reporting accounts), the
  housekeeping tables, and the views with no table of their own.
- **`tests/Test-SchemaCompatibility.ps1`** (in CI) and two sections of the SQL integration test: schema
  compatibility, and one agent with two identities, which runs the workbook load end to end through a stand-in for
  the Excel reader.
- **`tools\Import-AgentWorkbook.ps1 -ConnectionString`**, for the tests, which sign in to SQL Server with a login.

### Changed
- **The agent workbook load prefers the link an agent had.** When a second identity of an agent turns up that would
  match better, the agent keeps its key and the newcomer is folded into it.
- **`tools\Invoke-CopilotMaintenance.ps1 -Task NameAgent`** names the agent whichever of its identities is given.
- **CI checks out the whole history for the SQL integration job**, for the release tags it installs over.
- **The schema documents follow this release**: `docs/SCHEMA.md` (102 tables, 37 views, 1,324 columns, from 19
  scripts), `docs/REPORTING.md` and `docs/REPORTING-REFERENCE.md`, and both Word versions.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- **Checked on test data shaped like the client's, not on the client's.** The three cases were taken from query
  results a client sent (the ids, the workbook's columns, the counts) and reproduced in the tests. The first run on a
  real database, and the minutes it takes at 120 million interactions, were not measured.
- **The workbook case needs the workbook's row to hold both ids** (`SchemaName` with `EnvironmentId`, and `TitleId`).
  An agent the workbook does not list, or lists without a title id, stays as the log names it.
- **A merge is not undone.** If a workbook row was wrong, clear `MergedIntoKey` by hand; the interactions already
  moved stay with the agent until `bi` is rebuilt (`-Task RebuildBi`).
- **Microsoft's agents are merged on the name in their id alone.** Two different Microsoft agents given the same
  name segment would be counted as one.
- **Removing roles was checked on the demo company**: the page's markup, both requests and the audit log. The tick
  boxes and *Select all on this page* were not clicked in a browser by a test.
- **Compatibility is checked on the shape of the schema**: not on the meaning of a column whose name and type stay
  the same, nor on indexes and keys, nor on `audit.CopilotAudit` as the backfill's finalize script builds it.
- **The usage map of the `bi` schema was written from the code**, by which files read each table and view; it names
  the dashboard's report areas, not every page.
- The limits 2.6.2 listed for the two usage loaders, the name search and the mail log still stand.

## [2.6.2] - 2026-10-08

Every box that takes a person searches by the start of a first or a last name, from the third letter; Admin › Email
keeps the conversation with the mail server, to troubleshoot email that does not arrive; first plumbing to load the
usage and cost of GitHub Copilot and of Microsoft Foundry and Azure OpenAI; both certificates on Admin › Health; and
one name for a country the directory spells several ways.

To upgrade from 2.6.1 (or 2.6.0): with the nightly task disabled, replace the job server's scripts, run
`Install-CopilotAudit.ps1 -SqlServer <server> -Dashboard` once (it adds `audit.PipelineCertificate`, the eight tables
of `Create-CopilotExternalUsageSchema.sql` and the column `dash.NotificationLog.Detail`; nothing is rebuilt and no
data changes), then install the dashboard package over the one in place and enable the task again. The dashboard
works over a database not yet upgraded: it then keeps no mail conversations and shows the one certificate. The two
new loaders do nothing until they are set up: a nightly run whose settings name neither skips both, with a line each
in its log.

### Fixed
- **One country showed as several where the directory spells it several ways.** A client's Regions › Countries and
  offices had *USA*, *United States* and *United States of America (the)* as three countries, each with its own
  adoption. The directory load (`tools\Export-UsersWithHierarchy-ToSql.ps1`) now gives a country one name however
  Active Directory spells it: its two- or three-letter ISO code, its English name, that name with *(the)* after it
  or turned round as ISO's own list writes it (*Korea, Republic of*), with dots or in any case (*U.S.A.*, *usa*),
  and spellings in common use (*UK*, *Great Britain*, *Holland*, *Viet Nam*). The name is the English one a
  two-letter code already got (*United States*, *United Kingdom*). A value that names no country known is loaded as
  it is. Every report, the Power BI model and the `bi.Users` view read the loaded name, so all of them count the
  country once from the night after the upgrade; nothing is rebuilt and no table changes.

### Added
- **A person is found by three letters of a first or a last name.** Every box that takes a person now works one way:
  Compliance › Person record, the two managers of Leadership › Compare two teams, and on the Admin pages the person
  to grant or check in Access, an agent's owner, and a manager's digest. Nothing is searched until the third letter;
  then the names whose first name or last name begins with what was typed are offered, up to 20 (*mor* finds Avery
  Morgan and Morgan Petrov, not Seymour Hill; *avery mor* narrows it). A sign-in matches from its start too, and one
  typed in full is taken as it is. Who is offered is who the page may show: the viewer's part of the organization on
  a report, managers with a team at least the minimum group size on Compare, the whole directory on Admin pages. In
  privacy mode no report offers names. Each search is in the audit log (kind *Lookup*).
- **The mail server's conversation, on Admin › Email.** For every test message, and for every message that is not
  sent, the dashboard keeps what was said: the server, port and security it used and who signed in, each command it
  sent and each answer, and how it ended, with every cause of a failure. A new panel, *Troubleshooting: where the
  logs are*, shows the last test's conversation whole, names the other places to look (`dash.NotificationLog`, the
  web server's Windows Application log) and says what the common answers mean. *Recently sent* has a *Conversation*
  column with each kept conversation under **show**. What follows `AUTH` is masked and the message's own lines are
  left out: neither a password nor a digest's figures are kept. New column `dash.NotificationLog.Detail`.
- **GitHub Copilot usage and cost: `tools\Import-GitHubCopilotUsage.ps1`.** For a GitHub enterprise (Copilot
  Enterprise or Business), or one organization, from GitHub's REST API: every seat (`audit.GitHubCopilotSeat`), each
  day's usage metrics report (`audit.GitHubCopilotDay`, with the whole record as JSON), each person's day with the AI
  credits they used (`audit.GitHubCopilotUserDay`), and the billing usage lines for Copilot, this month and the last
  (`audit.GitHubCopilotCost`: quantity, price, gross, discount and net, in US dollars). The token is saved once on
  the job server (`-SaveToken`), encrypted for that computer, and is sent to GitHub's API only, never to a report
  file's address. `docs/GITHUB-COPILOT-USAGE.md` lists the values needed and has a part to hand to the enterprise
  owner: the policy to turn on, the token to create and its two scopes.
- **Foundry and Azure OpenAI usage and cost: `tools\Import-FoundryUsage.ps1`.** For the Azure subscriptions named:
  every model deployment of their Azure OpenAI and Foundry accounts (`audit.FoundryDeployment`), requests and input,
  output and total tokens per deployment, model and day from Azure Monitor (`audit.FoundryUsageDay`; the current
  metrics, or the older Azure OpenAI ones for an account that reports none), and the actual cost per account, meter
  and day from Cost Management (`audit.FoundryCostDay`). It signs in as the pipeline's own app with its certificate,
  or as an app of its own (`FoundryTenantId`, `FoundryClientId`, `FoundryCertificateThumbprint`), whose certificate
  Admin › Health then tracks with the others. `docs/FOUNDRY-USAGE.md` lists the values needed and has a part to hand
  to the Azure administrator: the two reader roles to assign.
- **Both loaders are optional steps of the nightly run.** Each is the run's last step when
  `copilot-audit.settings.psd1` names its source (`GitHubEnterprise` or `GitHubOrganization`; `FoundrySubscriptions`).
  **Where the settings do not, the run skips it and notes that in its log**, with the setting that turns it on.
  **A loader that fails does not fail the night**: its step row keeps the exit code, the log says the step is
  optional, and the run ends as it would have. Each part of a load is read on its own, so a part the token or the
  app may not read is left out and named, and the run ends *Partial*. A load can be run again over the same days:
  each table's rows are replaced in one transaction.
- **`audit.ExternalUsageRun`**, one row per run of either loader, and **Admin › Health › Other AI usage: GitHub
  Copilot and Foundry**: each source's last run, the days and rows, the newest day held and, for GitHub, when the
  token expires; amber for a partial load, a token within 14 days of expiry or no load for two days, red for a failed
  one with the service's own message. A source never loaded says it is optional and names its loader.
- **`Create-CopilotExternalUsageSchema.sql`** creates the eight tables; `Install-CopilotAudit.ps1` runs it, and the
  dashboard's account may read them.
- **`tests/Test-ExternalUsage.ps1`** (in CI), and a section of the SQL integration test that runs both loaders end
  to end against a stand-in for each service.
- **Both certificates on Admin › Health.** The pipeline signs in with two: one for the app that reads the audit log
  from Microsoft Purview, and one for the app that fetches the agent workbook from SharePoint Online. Health tracked
  only the first. *Certificates and audit searches* now has a card for each, green, amber inside the warning days
  or red once expired, and **Certificate details** sets the two side by side: what each is used for, subject,
  issuer, thumbprint, valid from, expiry, the app registration and tenant it signs in as, the store it is in, the
  job server, when it was last checked, and how to renew it. A certificate the settings name that the job server
  does not hold is shown as not found, in red.
- **The expiry alert watches each certificate on its own**: at *Warn about the app certificate* days (30), 14 and 3,
  and when it has expired, naming the certificate and what stops when it does; and once when the settings name one
  the job server does not hold. The audit app's keeps the state it had, so the upgrade sends no warning twice.
- **`audit.PipelineCertificate`**, one row for each certificate, rewritten by `Invoke-CopilotPipeline.ps1` at the start
  of every run (`Create-CopilotAuditSchema.sql`; `Install-CopilotAudit.ps1` adds it). A workbook fetched from a share
  or an address signs in as the task's account, with no certificate, and has no row. Until the job server and the
  database are upgraded, Health and the alert go on showing the one certificate each run recorded with itself.
- **`CountryNames` in `copilot-audit.settings.psd1`** (`-CountryNames` on the directory load): your own names,
  spelling = name, on top of the ones the script knows. A name given there wins, and reaches the script's own name
  for a country too: `'United States' = 'USA'` makes every spelling of it *USA*.
- **The directory load's log lists what it renamed**, with how many people each: *Countries given one name: USA ->
  United States (4,120); United States of America (the) -> United States (212)*.

### Changed
- **Leadership › Compare two teams no longer carries every manager's name in the page.** Its two boxes offered up
  to 2,000 managers from a list sent with the page; they now ask for the matches as you type, as every other box.
- **`Invoke-CopilotPipeline.ps1` has optional steps.** A step marked optional that fails is logged and recorded, and
  the run goes on. Only the two new loaders are optional; every other step stops the run as before.
- **The schema documents follow this release**: `docs/SCHEMA.md` (102 tables, 37 views, 1,323 columns, from 19
  scripts; a new section for the eight tables), `docs/REPORTING.md` and `docs/REPORTING-REFERENCE.md` (the new
  tables among the other layers, the two nightly steps, the scripts' text), and both Word versions.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- **Neither loader has called the real service.** Both run end to end in the tests against stand-ins that answer as
  GitHub and Microsoft document their APIs (seats over pages, report files, billing lines; accounts, deployments,
  metrics, cost, throttling, a refused token). The first real load of each should be checked against the service's
  own pages, as its guide says. Not verified at all: the hosts GitHub's report download links point to, and the
  fields inside a usage report beyond those the columns use.
- **No report reads the new tables yet**, and nothing in `bi` or `dash` does. A reporting tool can, with `SELECT` on
  them, which the `CopilotReporting` role does not hold: an administrator grants it table by table.
- **A GitHub login is not joined to a Microsoft 365 sign-in**, so GitHub's figures cannot be cut by department or
  manager. Foundry's usage has no person at all: Azure Monitor reports by account, deployment and model.
- **Foundry's cost is by account and meter, not by deployment**, and only accounts of kind *OpenAI* and *AIServices*
  are read.
- **Days in the new tables are UTC days**, as each service reports them, not the reporting time zone's.
- **The GitHub token is encrypted for the job server, not for one account** (Windows data protection, machine
  scope): restrict who can read its file, as the guide says.
- **The name search was checked through its address and the page's markup, on the demo company**; the script that
  asks as you type was read, and the layout audit loaded every page with it, but no keystrokes were typed in a
  browser by a test.
- **The mail conversation was checked against a stand-in relay** on this machine, with a sign-in accepted and one
  refused, without TLS. A real relay's STARTTLS exchange is logged by the same library and was not exercised.
- The Word documents and the guides' SQL carry the limits 2.6.1 listed.

## [2.6.1] - 2026-10-07

The two reporting guides as Word documents, and both guides brought up to this release. Nothing in the scripts, the
databases or the dashboard changes.

To upgrade from 2.6.0: nothing to run. The job server's scripts, the SQL scripts and the dashboard package are those
of 2.6.0. The Word documents are attached to the release, and are in the source zip's `docs` folder.

### Added
- **`docs/REPORTING.docx` and `docs/REPORTING-REFERENCE.docx`**: the brief guide (9 pages) and the reference (about 250)
  as Word documents, to hand to a reporting team that does not read the repository. US Letter; the headings are
  Word's own, so the Navigation pane lists them; a table's heading row repeats on each page; the scripts are set in
  a monospace block. Both are attached to the release as files of their own.
- **`tools/dev/schema/build_docx.py`** writes them from the Markdown (it needs `python-docx`), and the release
  checklist says to run it whenever a guide changes.

### Changed
- **Both guides are written for this release.** The reference's column tables and script text already followed 2.6.0
  (the two columns of `audit.CopilotLoad`, the load procedure); its guide now says what those columns hold, and both
  say where the Word versions are. Every `sql` block of both was run again, against a demo database of this release
  as a member of `CopilotReporting`: 63 blocks, none with an error.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- **The Word documents were checked by opening them in Word and reading them back, not page by page**: their
  headings, tables, lists and code blocks are all there and no Markdown is left in the text. A link is its text,
  not a link.
- **Nothing in CI reads a Word file**, so the two can fall behind the Markdown if the checklist step is missed. The
  Markdown guides are the ones the build keeps true to the scripts.
- The guides' SQL was not run inside Grafana, as in 2.5.9.
- **Not run for this release, as nothing they check changed:** the layout audit of the dashboard's pages, and the
  Power BI model.

## [2.6.0] - 2026-10-07

Two new Leadership reports, one to set two managers' teams side by side and one that gathers what the other reports
say to do; clearer names for figures and columns; two reports folded into the page beside them; and Admin › Health's
*Audit searches* card says what the last load took, where it read *0 submitted* after every good night.

To upgrade from 2.5.9 (or 2.5.8): with the nightly task disabled, replace the job server's scripts, run
`Install-CopilotAudit.ps1 -SqlServer <server> -Dashboard` once (it adds two columns to `audit.CopilotLoad` in both
databases; nothing is rebuilt and no data changes), then install the dashboard package over the one in place. The
dashboard works over a database not yet upgraded, and shows the last load without its count of searches until it is.

### Added
- **Leadership › Compare two teams.** Two managers' teams side by side, each manager with everyone under them, over
  the same period: people, Premium license holders, adoption, the share using Copilot and active in the last 7 days,
  interactions and days active per active person, prompts per session and sessions with a follow-up, each with the
  difference between the two in words (*57 more*, *9.3 points lower*); both teams' share of people using Copilot and
  interactions per person over time; and each app's share of each team's use. The side-by-side table downloads as a CSV, as does the list on What to act on. The managers are chosen by name from
  a list of those in the viewer's part of the organization, or by sign-in. The rules that decide whose part of the
  organization a viewer may open decide it here: a manager outside the viewer's part, a team with fewer enabled
  people than the minimum group size, and any team in privacy mode are not shown. No table or column is added for it.
- **Leadership › What to act on.** What the other reports say to do, as one list of counts for the viewer's part of
  the organization: license holders with no licensed use in the last 30 days, license holders to train, regular
  users of the free Copilot Chat ready for a license, Copilot licenses on non-human accounts, parts of the
  organization more than 10 points under the adoption around them (the lowest three named), adoption targets
  behind or missed, and, for holders of the Security role, jailbreak attempts and prompt injections. Each finding
  says why it matters and links to the report with the detail; a report the viewer cannot open is named with the
  role it needs. A finding with nothing to report is left out. No one is named.

### Changed
- **Two reports joined the page beside them**, so each subject is on one page. Nothing they showed is lost, and an
  old address or bookmark to either is answered *Page not found*, with the reports nearest to it.
  - *Security › Security signals by unit* is the last panel of **Security › Security signals**: the rate per 1,000
    interactions for each unit at each point, under the period's rate by unit that page already had.
  - *Governance › Copilot controls* is part of **Governance › Models and controls** (was *Models and web
    search*), which already had its three headline figures: memory updates over time, deferred data-loss checks
    over time, and web search by unit are panels there.
- **Clearer figure names.** 41 headline figures that were one word or a fragment say what they count: *Top* is
  *Highest adoption*, *Open* is *Open flags*, *Cost* is *License cost*, *Assigned* is *Licenses assigned* or *Seats
  assigned*, *Individual contributors'* is *Individual contributors' adoption*, and so on. The same thing has one
  name: *Grounded on the web* is *Searched the web* everywhere, and *Unused for 30+ days* is *Unused for 30 days or
  more*.
- **The page links under a long list mark the pages left out with a dot, not an ellipsis**, which the dashboard keeps
  for nothing: a label is shown whole.
- **Column headings say their window the same way.** *Days used (30)*, *Interactions (30)*, *Days used (of 30)* and
  *Days of use, last 30* all read *..., last 30 days*, on Upgrade, train or revoke, Rollout waves, License
  optimization, Fading use and the lists beside them; a CSV downloaded from those lists has the new headings.
  *Signals* is *Security signals*, and three one-word headings name their column (*Called by*, *Server*, *Dataset*).

### Fixed
- **Admin › Health said *0 submitted to Microsoft Purview in the last 24 hours* after every night that succeeded.**
  The *Audit searches* card counted the rows of a load's search windows, and the merge empties those when the load
  is done: the card could only show a load that was still running or had stopped, and read as if nothing had been
  fetched. It now says what is waiting, then what the last load took: when it was merged, the records it fetched
  and how many were new, and in how many searches. `audit.usp_MergeCopilotLoad` keeps that count with the load, in
  two new columns of `audit.CopilotLoad` (`Searches`, and `SearchesResubmitted` for those Microsoft Purview had to be
  asked for more than once); a load merged before shows its records without them, and a dashboard over a database
  not yet upgraded does the same.

### Removed
- **Security › Security signals by unit** and **Governance › Copilot controls**, each now part of the page beside it
  (see Changed). Their addresses answer *Page not found*, with the reports nearest to them. A setting that turned
  either off in Admin › Reports shown is left in place and does nothing.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- **The least-used reports are not removed.** These seven stay, and are the ones to turn off in Admin › Reports
  shown where they are not wanted: Enablement › Conversation depth, Regions › Working patterns, Adoption › Drops and
  spikes, Forecasting › License action outcomes, Compliance › Labeled content by unit, Adoption › Retention by
  license month, Agents › Agent hand-offs. The list is a judgment from what each shows, not from how often it is
  opened: the audit log of page views (`dash.AuditLog`) gives the real ranking on an install.
- **Compare two teams compares managers' teams, not departments.** A team is a manager and everyone under them, from
  the org chart.
- **Not tested:** page speed of the two new reports at 40 and 120 million interactions (they read the daily rollups
  and, for the app mix, the interactions of the two teams for the period), and the Power BI model in Power BI
  Desktop, which this release does not change.
## [2.5.9] - 2026-10-07

Two guides for a team that builds its own reports on the databases, in Grafana or any other tool that reads SQL
Server: a brief one, and a reference with every table and view and the text of the scripts that create them.
Nothing in the scripts, the databases or the dashboard changes.

To upgrade from 2.5.8: nothing to run. The job server's scripts, the SQL scripts and the dashboard package are
those of 2.5.8, so an install of 2.5.8 can stay as it is. The guides are in the source zip's `docs` folder, and in
the repository.

### Added
- **[docs/REPORTING.md](docs/REPORTING.md): reporting on the Copilot data, the brief guide**, about ten pages. What
  the two databases and their three schemas hold; how and when the data arrives, and what that means for a report
  (once a night, the last two days not final, a full rebuild, the ways history is restated); the 26 `bi` views
  that are the reporting interface and how they join; the reporting role and how to get a Windows account or a
  SQL login into it; ten rules every query follows; the figures as the dashboard and the Power BI model define
  them; Grafana's data source and dashboard settings, the two conversions between Grafana's UTC range and the
  data's local days, variables and annotations; nine queries to start from; privacy mode, small groups, and that
  nothing in SQL limits which people a reporting account reads.
- **[docs/REPORTING-REFERENCE.md](docs/REPORTING-REFERENCE.md): the reference**, in three parts.
  - *Part A, the guide*: the system in one page; how `Install-CopilotAudit.ps1` creates the schema, script by
    script; which procedure fills which table each night, how much a night rewrites and what a report must expect;
    access, with what a member of `CopilotReporting` can and cannot read and the grants an administrator may add
    by hand; every reporting view column by column, with what each column means; each figure as SQL; some forty
    Grafana queries (usage, breakdowns, adoption and licenses, sessions, agents, security and content, a viewer's
    own people only, freshness); the other layers and when a report needs them; and how to check a report against
    the dashboard.
  - *Part B*: every table and view, column by column, the text of `docs/SCHEMA.md`.
  - *Part C*: the full text of the installer, the 18 SQL scripts, the nightly pipeline, the agent workbook import
    and the dashboard's installer, in the order a new install runs them.
- **`tools/dev/schema/Build-ReportingReference.ps1`** writes Parts B and C from `docs/SCHEMA.md` and the scripts,
  and **`tests/Test-ReportingReference.ps1`**, in CI and the release, fails when the reference is behind them, when
  a view the scripts create in `bi` is not described in the reference and named in the brief guide, when either
  names something in `bi` that no script creates, or when the brief guide passes 20 pages.

### Changed
- **docs/SCHEMA.md counts what it describes**: 93 tables (and the 2 left over from 1.x), 37 views and 1,208 columns.
  It said 90 and 1,162, the counts from before the agent workbook's three tables and the columns added since.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- **The guides' SQL was not run inside Grafana.** Every `sql` block of both ran against the demo database as a member
  of `CopilotReporting`, with Grafana's macros (`$__timeFrom()`, `$__timeTo()`) and the `tz` variable replaced by
  fixed values: 63 blocks, none with an error (the one over `bi.AgentEvents` returns no rows there, as the demo has no Copilot Studio records). The data source settings, the macros and `${__user.login}` are as
  Grafana documents them; check them against the Grafana version in use.
- **A reporting account cannot read the reporting time zone, the refresh log or whether a rebuild is under way.**
  The role reads the `bi` views only. The guides say to ask the administrator for the zone, and give two grants an
  administrator may add by hand (`bi.RefreshRun`, `bi.RebuildInProgress`); the installer does not make them.
- **Not run for this release, as nothing they check changed:** the layout audit of the dashboard's pages, and the
  Power BI model against the demo database.

## [2.5.8] - 2026-10-06

The agent workbook set beside the audit log and the directory: agents whose owner has gone, agents whose settings
disagree with their use, and autonomous agents no longer counted as unused. Every Owner column shows names, every
long list pages the same way, and three reports are simpler.

To upgrade from 2.5.7: replace the job server's scripts, run `Install-CopilotAudit.ps1 -Dashboard` once (it adds five
columns to `dash.AgentInventory`; nothing is rebuilt), install the dashboard package over 2.5.7, then run
`.\tools\Import-AgentWorkbook.ps1` once, or wait for the night, to fill them. Until `Install-CopilotAudit.ps1` has run,
the import stops and says so.

### Added
- **Agents › Agents without an owner.** The workbook's agents whose owner (`OwnerIdUPN`) is not in the directory loaded
  from Active Directory (most likely they left), has a disabled account, or is not set, the most used first, with the
  owner's manager (who to ask for a new owner) and department. Below, every agent by its owner's department: agents,
  those used in the period, their interactions and those without an active owner.
- **Agents › Agent checks**: where an agent's settings in the workbook and its use in the audit log disagree, each a
  *breach* to act on or a *review*:
  - quarantined (`IsQuarantined`), but used in the period (breach);
  - web search off (`IsWebSearchEnabled`), but its interactions searched the web (breach);
  - no authentication (`Authentication`), but used (review);
  - its owner left the directory or is disabled, but it is still used (review);
  - in a developer, trial or sandbox environment (`EnvironmentSku`) and used by 25 people or more: move it to production (review);
  - on the AgentsInProd sheet, but not used in the period and not autonomous (review).
  Each check's count is a figure at the top, and the list can show one check at a time.
- **Autonomous agents.** An agent with triggers or flows (`Triggers`, `Flows`) can work with nobody chatting to it, so
  with no interaction in the period it is *Autonomous (no chat)*, not *Not used*: on Agent inventory, Shared agents,
  Agents in production and Agent detail, in the *Not used* figures and the *Not used in the period* filter, and in
  Agents in use's *Listed agents not used*. Agent inventory counts them in a figure of their own.
- **A department filter on Adoption › Departments and Enablement › Champions**, every department by default.

### Changed
- **Every Owner column shows the owner's name**: Agents in use, Agent inventory, Shared agents, Agents in production,
  Build and publish, Agents costing credits, Agents without an owner, Agent checks and Security's triage. The name
  comes from the directory loaded from AD (owners who never used Copilot included), else the report tables; a
  sign-in neither knows is shown as it is. Admin › Reference data still takes a sign-in.
- **One way of paging, on every report.** A list paged on the server had *1–50 of N, Previous, Next* above it, beside the
  search box; a long table paged in the browser (2.5.7) had numbered pages under it. Both now have the same links
  under the list, in the same look: the count, Previous, the first and last pages and those around this one, and
  Next, 50 rows to a page. The search box and the sort stay above a server's list.
- **Licensing › License pool has no filter bar**: its seats are the tenant's as they stand, which no period or part of
  the organization changes.
- **Adoption › Leaderboard no longer ranks by company**: by department, division, country or office.

## [2.5.7] - 2026-10-06

The workbook's agents are matched to the audit log's by the identities the audit log really gives them, so far
more of them are found; the agent pages' counts agree; agents of one name are told apart by their environment.
The offline zip carries ImportExcel, Agents in production names owners, and every long table is shown 50 rows
at a time.

To upgrade from 2.5.6: replace the job server's scripts from the offline zip (it brings `Modules\ImportExcel`),
run `Install-CopilotAudit.ps1 -Dashboard` once (it adds four columns to `dash.AgentChildLink`; nothing is
rebuilt), and install the dashboard package over 2.5.6. A copy of ImportExcel saved into `Modules` by hand can stay
or go; the zip's is the same version. Then run `.\tools\Import-AgentWorkbook.ps1` once, or wait for the night: it
matches every agent again, and prints how. Until `Install-CopilotAudit.ps1` has run, the import stops and says so.

### Fixed
- **Most of the workbook's agents were not matched to their use.** 2.5.6 matched on the schema name an audit
  identity ends in, cut at its last hyphen, and on GUIDs inside it. The audit log names a Copilot Studio agent
  `Copilot.Studio.<EnvironmentId>-<SchemaName>` (the default environment's id is `Default-<tenant id>`) and an Agent
  Builder or Copilot Studio lite agent `Copilot.Studio.Declarative.<TitleId>`, so a schema name with a hyphen in it
  and every declarative agent were missed. An agent is now matched, in order: on its environment and schema name;
  on its title id (with the `T_` added where the workbook leaves it off); on the schema name Copilot Studio's own
  authoring records give its bot id (`audit.AgentEvent`), with its environment; then, loosely, on the schema name
  alone, the bot id or the Entra agent id. A loose match is taken only where one workbook agent claims the audit
  log's agent: a schema name used in two environments matches neither, and is counted. Each load prints how many
  agents each rule matched, and how many were left.
- **Agent inventory and Agents in use counted unused agents differently.** Agents in use counted the audit log's
  agents that one workbook agent was shown against; Agent inventory counted the workbook's agents. Both now count
  the workbook's: *Not used in the period* on one equals *Listed agents not used* on the other, and *Agents used*
  says how many of them the workbook lists and how many it does not (deleted, unmatched, or Microsoft's own).
- **Child agent usage merged different children of one name.** It grouped children by name, and Copilot Studio calls
  every new child agent "Agent", so the children of four parents in four environments were one "Agent" with four
  parents. A child is now what the ParentChildAgent sheet says it is: a *child agent* (RelationshipType without
  "Connected") lives inside its one parent and is known by its component id; a *connected agent* is an agent of its
  own, which several parents can call, known by its row in the inventory (its environment and the schema name
  `SchemaName` ends in) or by that pair. Children that share a name are told apart: a child agent by its parent
  (*Agent (in IT Helpdesk)*), a connected agent by its environment (*Brief Word Renderer (Sales Dev)*). Parents use
  the inventory's names, so two parents of one name are told apart by their environment too, and so is a parent the
  inventory does not list (*Advisor (Developer · 015d2539)*: the sheet's EnvironmentSku and the start of its
  EnvironmentId, where the inventory has no name for the environment). The report has a
  *Kind* column and the parent's environment, and a connected agent links to its own detail. `dash.AgentChildLink`
  gains `ChildKey`, `ChildKind`, `ChildInventoryAgentId` and `ParentEnvironment`.

### Changed
- **The start page is a page of areas, not of every report.** Each area (Leadership, Adoption, Licensing and the
  rest) is one large card in a grid: its name, who it is for and how many reports it holds. Selecting a card opens
  its reports in a window, under their headings and each with what it answers, as *About these reports* does; Escape,
  its × or a click outside closes it. A card's *Start with* opens the area's first report in one click. Your most
  used reports are a row of up to six shortcuts above the cards, and the sidebar's *Your most used* stays folded on
  the start page. The access notice is one line, its detail behind *More*. Without script, a card opens the area's
  first report.
- **Agents of one name show their environment**: two workbook agents called *HR Assistant* are *HR Assistant (HR
  Prod)* and *HR Assistant (Sales Dev)*, on every page and in Power BI; with the environment the same too, the schema
  name follows it. An agent whose name is its own is shown as it is. The workbook's own name stays in
  `dbo.AgentInventory`.
- **Agents in production shows each owner by name**: the directory's display name for the workbook's `OwnerIdUPN`,
  or the UPN where the directory does not have the person.
- **Every report's long tables show 50 rows at a time.** A table the server sends whole, of more than 50 rows,
  now shows a page in the browser, with the row count, the pages around this one and Previous and Next under it.
  Sorting by a column orders every row and starts again from the first page; a total row stays on each page; a
  print and the CSV download hold every row. Lists the server pages (Agent inventory, an agent's users, the audit
  log and the rest) were 50 to a page already.

### Added
- **ImportExcel in the offline zip** (`copilot-audit-backfill-v2.5.7-offline-win-x64.zip`, `Modules\ImportExcel\7.8.10`),
  so `tools\Import-AgentWorkbook.ps1` runs on a server with no gallery access. ImportExcel and the EPPlus library
  in it are not signed, so `tools\Save-CopilotAuditModules.ps1` pins the version with a SHA-256 of every file in it
  and refuses a download that differs; the Graph module's signature check is as before. The bundle's
  `NOTICE-BUNDLED-MODULES.txt` names both, with their licenses (Apache 2.0; EPPlus, LGPL). PnP.PowerShell, for
  SharePoint Online, is still saved by hand.

## [2.5.6] - 2026-10-06

The agent workbook becomes the list of agents. It is fetched each night from SharePoint Online (the newest
workbook in a library, as an app of its own) or from a share; the reports name and own agents from it, show
which are shared and which are in production, flag the agents no one used, and follow connected agents from
parent to child. Admin › Health says when it last reached the tables, and each nightly run writes a log of
its own.

To upgrade from 2.5.5 (or 2.5.4):

1. Replace the job server's scripts, keeping `copilot-audit.settings.psd1` and `logs`.
2. Run `Install-CopilotAudit.ps1 -Dashboard` once. It adds `dash.AgentInventory`, `dash.AgentChildLink` and
   `dash.AgentInventoryRun`, and replaces the `dash.Agent` view and `bi.usp_NameAgents`. Nothing is rebuilt.
3. Install the dashboard package over 2.5.5.
4. For the workbook from SharePoint Online:
   - Register an app for it (or use an existing one) with a certificate, and give it SharePoint's
     **Sites.Selected** application permission, admin-consented; a SharePoint administrator then grants it
     read on the site: `Grant-PnPAzureADAppSitePermission -AppId <its client id> -DisplayName 'Copilot agent
     workbook' -Site <site address> -Permissions Read`. (Or Sites.Read.All, for every site.) The certificate
     goes in the job server's LocalMachine\My, readable by the task's account.
   - Put PnP.PowerShell beside the scripts with ImportExcel: `Save-Module ImportExcel, PnP.PowerShell -Path
     D:\CopilotAudit\Modules`. PnP.PowerShell needs PowerShell 7.4.6 or later.
   - Add to the settings file:
     ```powershell
     AgentWorkbookSiteUrl               = 'https://contoso.sharepoint.com/sites/AI'
     AgentWorkbookFolder                = 'Shared Documents'      # the Documents library, or a folder in it
     AgentWorkbookFilter                = '*.xlsx'
     AgentWorkbookClientId              = '<the app''s client id>'
     AgentWorkbookCertificateThumbprint = '<its certificate''s thumbprint>'
     AgentWorkbookTenantId              = 'contoso.onmicrosoft.com'   # only when not the TenantId above
     ```
   - Run `.\tools\Import-AgentWorkbook.ps1` once, as the task's account: with no parameters it reads the
     settings file, as the nightly run does.
   From a share instead, `AgentWorkbookUrl = '\\fileserver\Copilot\Agents.xlsx'` is enough.

### Added
- **The newest workbook from SharePoint Online.** `tools\Import-AgentWorkbook.ps1` signs in with PnP.PowerShell
  as its own app registration and certificate, kept in the settings file apart from the pipeline's app
  (`AgentWorkbookClientId`, `AgentWorkbookCertificateThumbprint`, and `AgentWorkbookTenantId` when the site is in
  another tenant), and takes the most recently created file matching `AgentWorkbookFilter` in
  `AgentWorkbookFolder`. Run with no parameters it reads everything from the settings file.
- **Shared agents** (Agents): the workbook's agents with at least one viewer (`ViewerUsers` or `ViewerGroups`
  not 0), their viewers and editors, and their use in the period; unused ones flagged.
- **Agents in production** (Agents): the agents on the AgentsInProd sheet, their channels and their use in the
  period from the audit log; unused ones flagged.
- **Child agent usage** (Agents): each child agent on the ParentChildAgent sheet, how many parent agents call it
  and which, each parent's people and interactions in the period, and the child's totals across its parents
  (interactions added up, people counted once); a choice of one child.
- **Admin › Health › Agent inventory (Excel)**: when the workbook last reached the tables, how many agents and
  how many the audit log knows, the file and when it was created, marked fresh, stale or old like the other
  steps; a later load that failed is named with its reason. Each load is kept in `dash.AgentInventoryRun`.
- **`dash.AgentInventory` and `dash.AgentChildLink`**, the tables the agent reports read, filled from the sheets
  on each load. The sheets themselves are still loaded as they come into `dbo` (AgentInventory, AgentsInProd,
  ParentChildAgent), emptied and loaded again each night.

### Changed
- **Agent names and owners come from the workbook.** The name is the workbook's `AgentName`; where it is
  empty, or the workbook does not list the agent (one since deleted), a name set in Admin › Reference data, else
  the audit log's. The owner is the workbook's `OwnerIdUPN`. This holds on every page that names an agent, and in
  the report tables Power BI reads (`bi.usp_NameAgents`, run after each load). 2.5.5 wrote the names into Admin's
  own table (`dash.AgentAlias`, marked *Agent workbook*); the first 2.5.6 load removes those rows.
- **Agent inventory** lists the workbook's agents, all of them (paged, searchable, sortable): owner, environment,
  shared, in production, created, last published, last used, people and interactions in the period, and
  whether anyone used it, with a filter for those no one did. Without a workbook it is as before.
- **Agents in use** says whether each agent is shared and whether the workbook lists it: a Copilot Studio agent
  it does not list was deleted, or is not in it yet. A figure counts the listed agents no one used.
- **Agent detail** shows every column of the agent's row on the workbook's first sheet, whatever columns the
  workbook has (GUIDs included), and opens a listed agent no one has used, flagged as such.
- **A log for each nightly run.** `logs\nightly-<date>-<time>.log` (and `history-…`), where a second run on one day
  appended to the first's log.
- **The matching runs on equal keys**: the schema name, the first GUID and the title id an audit identity carries,
  against the workbook's `SchemaName`, `AgentId`, `TitleId` and `EntraAgentId`, so 13,000 agents match in
  seconds. `OwnerIdUPN` is read as the sample workbooks name it (2.5.5 read `OwnerUPN`).

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- The workbook lists Copilot Studio agents; Microsoft's own agents (Researcher, Analyst) keep the audit log's names.
- A new agent first used tonight is matched to the workbook the next night, after tonight's load has seen it.

## [2.5.5] - 2026-10-05

Agents under the names your inventory gives them. An Excel workbook of agents, kept outside the
audit log, is loaded into the Copilot database each night, and the dashboard names each agent from
it: a Copilot Studio agent the audit log knows only by `cre12_hrPolicyBot` shows as
*HR Policy Advisor*.

To upgrade: replace the job server's scripts. Nothing in SQL or the dashboard changed, and nothing
is rebuilt. The workbook is optional: without `AgentWorkbookUrl` in the settings the nightly run is
as before. To use it:

1. Put the ImportExcel module beside the scripts, so the nightly task's account finds it: on a
   machine that reaches the PowerShell Gallery, `Save-Module ImportExcel -Path D:\CopilotAudit\Modules`.
2. Add `AgentWorkbookUrl = 'https://intranet/agents.xlsx'` (an http(s) address the task's account
   can read, or a file or UNC path) to `copilot-audit.settings.psd1`.
3. Run `.\tools\Import-AgentWorkbook.ps1 -Url <the address> -SqlServer SQLPROD01` once by hand, as
   the task's account, and read its lines: each sheet's rows, then how many agents it named.

### Added
- **`tools\Import-AgentWorkbook.ps1`.** Downloads the workbook and loads its sheets, each into its
  own table in the Copilot database: by default `AgentInventory`, `AgentsInProd` and
  `ParentChildAgent` into the `dbo` tables of those names (`-Sheets` maps others). A sheet's first
  row names its columns. A table that does not exist is made, one text column for each heading,
  and a heading added later becomes a column. Each table is emptied and loaded again in one
  transaction, so a reader never sees it empty; a sheet that is empty or missing leaves its table
  as it was, so a bad download empties nothing.
- **Agent names from the inventory.** The script then names the dashboard's agents
  (`dash.AgentAlias`) from `AgentInventory`'s `AgentName`, with `OwnerUPN` as the owner. An agent
  is matched on the identity the audit log gives it: the schema name it ends in (`SchemaName`)
  first, then the bot id (`AgentId`), the title id (`TitleId`, such as `T_…`) or the Entra agent
  id (`EntraAgentId`) it holds. `BlueprintId` is not used: agents share one. A name an
  administrator set in Admin › Reference data is never changed; a name the workbook gave is
  updated when the workbook renames the agent, and removed when the workbook drops it. The
  dashboard shows the names at once. Power BI's agent names still come from the audit log.
- **A nightly step, *Agent workbook from Excel***, after the licenses, when the settings name
  `AgentWorkbookUrl`. It fails the night like any other step, so a workbook that cannot be read
  shows on Admin › Health.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- The download signs in as the task's Windows account, which suits an intranet site, SharePoint
  Server or a file share. A SharePoint Online or OneDrive link needs a sign-in the script does not
  make: copy the workbook to a share the account can read, and point `AgentWorkbookUrl` at it.
- `AgentInventory` must carry the columns `AgentId`, `AgentName`, `SchemaName`, `TitleId`,
  `EntraAgentId` and `OwnerUPN` for the naming; without one of them the sheets still load and the
  naming step fails.

## [2.5.4] - 2026-10-04

A night's refresh no longer grows with the license history, and two tools show where a night's time
goes. The session build and the report-table (`bi`) refresh each worked out every license change
from the records every night, and the session build looked each session up in them without an
index: with a license history of millions of events, both took long whatever the night's load. The
changes are now kept in a table as the license events are shredded, and sought.

One night's refresh in the lab, over 120 million interactions and a license history of 4,000,000
events, before and after; both wrote the same rows:

| Stage of the night | 2.5.3 | 2.5.4 |
|---|---|---|
| Sessions (60,852, from 329,445 records) | 22 min 36 s | 45 s |
| Report tables (220,480 interactions, 304,308 license changes) | 17 min 7 s | 2 min 24 s |
| Dashboard totals | 2 min 35 s | 2 min 9 s |
| The three together | 42 min 24 s | 5 min 24 s |

To upgrade: both databases and the job server's scripts change, so run `Install-CopilotAudit.ps1`
from this release with the nightly task disabled. It works out the kept changes once (about two
minutes for 4,000,000 license events in the lab). It also shreds every license record one last
time, to record that the shred finished, unless `-LicenseEventsShredded` says that it did (step 8);
later runs shred only what is new. The dashboard is 2.5.3's: a site on 2.5.3 can stay as it is, and
one on 2.5.2 or 2.5.1 installs this release's package for 2.5.3's daily charts. No figure on any
report changes. The steps, from 2.5.2, with a job server in `D:\CopilotAudit`, a site in
`C:\inetpub\CopilotInsights`, SQL Server `SQLPROD01` and the two databases under their usual names;
add `-TrustServerCertificate` (and `-C` to sqlcmd) where the settings file has it.

### To upgrade from 2.5.2, step by step

On the job server, in an elevated PowerShell 7 window, as an account that owns both databases, in
the folder holding the downloaded files:

| # | Command | What it does |
|---|---|---|
| 1 | `Disable-ScheduledTask -TaskName 'Copilot audit - nightly'` | Keeps the nightly run from starting during the upgrade. If `State` shows `Running`, or Admin › Health shows a run under way, let it finish first: the installer refuses to run beside it. |
| 2 | `sqlcmd -S SQLPROD01 -E -Q "BACKUP DATABASE [CopilotAudit] TO DISK = N'CopilotAudit-pre-2.5.4.bak' WITH COPY_ONLY, COMPRESSION, CHECKSUM"` | Backs up the Copilot database to SQL Server's default backup folder. `COPY_ONLY` leaves the regular backup chain alone. |
| 3 | `sqlcmd -S SQLPROD01 -E -Q "BACKUP DATABASE [CopilotLicenseEvents] TO DISK = N'CopilotLicenseEvents-pre-2.5.4.bak' WITH COPY_ONLY, COMPRESSION, CHECKSUM"` | Backs up the license-event database the same way. This release changes it. |
| 4 | `(Get-FileHash .\copilot-audit-backfill-v2.5.4-offline-win-x64.zip -Algorithm SHA256).Hash -eq (Get-Content .\copilot-audit-backfill-v2.5.4-offline-win-x64.zip.sha256 -Raw).Split(' ')[0]` | Prints `True` when the download matches its published hash. |
| 5 | `Unblock-File .\copilot-audit-backfill-v2.5.4-offline-win-x64.zip` | Removes the downloaded-from-the-internet mark, so the scripts are allowed to run. |
| 6 | `Expand-Archive .\copilot-audit-backfill-v2.5.4-offline-win-x64.zip -DestinationPath D:\ -Force` | Unpacks the release to `D:\copilot-audit-backfill-v2.5.4-offline-win-x64`. |
| 7 | `robocopy D:\copilot-audit-backfill-v2.5.4-offline-win-x64 D:\CopilotAudit /E /XF copilot-audit.settings.psd1 /XD logs` | Replaces the scripts with this release's. The settings file and the logs are left as they are. An exit code under 8 is success. |
| 8 | `Set-Location D:\CopilotAudit; .\Install-CopilotAudit.ps1 -SqlServer SQLPROD01 -Database CopilotAudit -LicenseDatabase CopilotLicenseEvents -Dashboard -LicenseEventsShredded` | Upgrades both databases. In the license-event database it adds an index and the table of kept SKU changes, and works them out once from the events already shredded: it says *Working out the SKU changes of the license events already here, once (2.5.4)*, then how many it kept and how long it took. **`-LicenseEventsShredded`** says that the last run of `Install-CopilotAudit.ps1` on this database ended with `Done.`, so every license record is shredded and whole: the installer records that, says *every one of … record(s) is shredded already, so none is read again*, and reads no license record. **Leave the switch out** if that run was stopped or failed part way, or if nobody can say: the installer then shreds every license record once more (it says *once more, to record that every one is shredded*, and takes as long as that step took in earlier upgrades), which mends any event a stopped shred left without its properties. Either way no later run reads them all again. In the Copilot database it replaces the session and report-table procedures, and rebuilds the dashboard's org chart and today's rollups. It ends with `Done.` With `SessionLicenseDatabases` in the settings, add `-LicenseEventDatabases` with the same list. |
| 9 | `sqlcmd -S SQLPROD01 -d CopilotLicenseEvents -E -Q "SELECT RunId, StartedUtc, EndedUtc, Events, Changes FROM audit.LicenseSkuChangeRun ORDER BY RunId; SELECT COUNT(*) AS Kept FROM audit.LicenseSkuChange; SELECT Name, Value FROM audit.PipelineSetting WHERE Name LIKE N'LicenseShred%';"` | Shows what step 8 did: one run (two when it shredded once more) with `Events` every license event, `Changes` and `Kept` the SKU changes kept for them, and one mark, `LicenseShredCompleteUtc`. A mark named `LicenseShredInProgress` means a shred did not finish: run step 8 again. |

On the web server, in an elevated Windows PowerShell 5.1 window, in the folder holding
`copilot-audit-backfill-v2.5.4.zip`, `copilot-insights-v2.5.4.zip` and their `.sha256` files:

| # | Command | What it does |
|---|---|---|
| 10 | `(Get-FileHash .\copilot-audit-backfill-v2.5.4.zip -Algorithm SHA256).Hash -eq (Get-Content .\copilot-audit-backfill-v2.5.4.zip.sha256 -Raw).Split(' ')[0]; (Get-FileHash .\copilot-insights-v2.5.4.zip -Algorithm SHA256).Hash -eq (Get-Content .\copilot-insights-v2.5.4.zip.sha256 -Raw).Split(' ')[0]` | Prints `True` twice: both downloads match their published hashes. |
| 11 | `Unblock-File .\copilot-audit-backfill-v2.5.4.zip, .\copilot-insights-v2.5.4.zip` | Lets the installer and the pre-cache script run. |
| 12 | `Expand-Archive .\copilot-audit-backfill-v2.5.4.zip -DestinationPath . -Force` | Unpacks the source package, which holds the dashboard installer, to `.\copilot-audit-backfill-v2.5.4`. |
| 13 | `.\copilot-audit-backfill-v2.5.4\dashboard\Install-CopilotInsights.ps1 -PackagePath .\copilot-insights-v2.5.4.zip -SqlServer SQLPROD01` | Updates the site. It keeps its database, its `TrustServerCertificate` setting, account, bindings, administrators and the pre-cache task's time. |
| 14 | `Get-ScheduledTaskInfo -TaskName 'Copilot Insights pre-cache'` | `NextRunTime` is the next 07:00 Eastern on the server's clock. Then open a report at 30 days: a trend chart now ends with a line that starts *Shown to*, which is 2.5.3's change and shows the site is on the new package. |

On the job server again, PowerShell 7, from `D:\CopilotAudit`:

| # | Command | What it does |
|---|---|---|
| 15 | `Enable-ScheduledTask -TaskName 'Copilot audit - nightly'` | Turns the nightly task back on. |
| 16 | `Start-ScheduledTask -TaskName 'Copilot audit - nightly'` | Runs it now, to time it on this release; or leave it to tonight. Start it at least an hour after the last run's load ended: a run that finds nothing new to load refreshes nothing. |
| 17 | `.\tools\Measure-CopilotNightly.ps1 -SqlServer SQLPROD01 -LogPath D:\CopilotAudit\logs` | During the run or after it: each step's time, each stage inside the two loads, and the statements that ran longest. `-RunId` with the id of a run from before the upgrade (they are in its first list) shows that one for comparison. Read-only. |
| 18 | `.\tools\Test-CopilotDataHealth.ps1 -SqlServer SQLPROD01` | Read-only check that every load and refresh is current. Its license section now says *The SKU changes sessions and bi read are worked out for every event shredded.* |

To go back: restore the two backups, or copy the earlier release's scripts over `D:\CopilotAudit`
and run its `Install-CopilotAudit.ps1`, which puts its view and procedures back; the new table and
index stay, unused.

### Fixed
- **Every night worked out the whole license history from the records, twice.** The session build
  and the `bi` refresh each read every SKU change in the license-event database, through
  `audit.vLicenseSkuChange`, which worked them out from the events and their properties at each
  read: which records are changes, which carry the license lists, which borrow them from a record
  raised with them, which SKUs each list has. Over 4,000,000 license events in the lab each of the
  two reads took 11 minutes on one core and read 196 million pages, whatever the night had loaded
  (run on its own, with its rows sent to a client, one read took 20 minutes and wrote 15 GB to
  tempdb). The changes are now kept in `audit.LicenseSkuChange`: `audit.usp_ShredLicenseEvents` ends
  by working out those of the events shredded since they were last worked out, and of the records
  raised with them. In the lab that takes 2 to 9 seconds for a night's 44,405 events, and each of
  the two reads under half a second. The rows are the same: checked against the old view's 300,000
  rows at that size, and case by case in the tests.
- **The session build read the whole license history for every session.** It kept the Copilot
  changes in a temporary table with no index, and asked it, per session, whether the person held a
  Copilot SKU when the session began. With 154,530 Copilot changes a night's 60,852 sessions took
  10 min 48 s on six cores (an hour of CPU) for that one question; with an index, seconds.
- **A night's accessed resources could be joined by reading all of them.** The `bi` refresh takes
  the resources of the night's interactions from `audit.RawLogAccessedResource`. Once that table is
  large SQL Server samples its statistics, and a sample counts far too few distinct records in a
  key whose rows for one record sit together (68,000 where there are 11.6 million, in the lab). On
  that, the night's 220,480 interactions were joined by reading all 69,628,759 rows on one core:
  4 min 11 s, against 7 seconds when each interaction's resources are sought, which the statement
  now asks for.
- **An upgrade shredded every license record again.** `Install-CopilotAudit.ps1` read and parsed
  every record of the license-event database on every run, to write the rows already there: the
  longest step of an upgrade on a large license history. It now shreds every record only for
  `-Rebuild`, or for a database that carries no mark that a shred of every record finished. That
  is every database an earlier release shredded, once: a shred that was stopped between a batch's
  events and their properties shows in no count, so the first run of this release shreds every
  record one more time and leaves the mark, unless `-LicenseEventsShredded` says that the last
  shred did finish. The switch is not taken where the tables show otherwise (a chunk group with
  every fragment, whose document holds properties, that was never reassembled). From then on,
  records that have no event yet are shredded from the earliest of them on, and nothing else is
  read.
- **A license shred that was stopped part way could leave events without their properties for
  good.** `audit.usp_ShredLicenseEvents` commits statement by statement: stopped between a batch's
  events and their properties, or before its chunk groups are reassembled, it left those events
  with no license lists, so their SKU changes were lost until every record was shredded again. A
  shred now marks that it is running and from when it shreds (`LicenseShredInProgress` in
  `audit.PipelineSetting`), and takes the mark away when it has finished and the SKU changes are
  worked out. The next shred, the nightly's or the installer's, shreds what the stopped one was
  shredding too, whatever it was asked for. Until then sessions and `bi` read the SKU changes of
  the last shred that finished.
- **Two license shreds could run at once** in one database (a CSV import beside the installer, or
  one run by hand). One shred now runs at a time: it holds a lock, as the `bi` refresh does, and a
  second waits for it, saying so. So a mark a shred finds is always one of a shred that was stopped.
- **A CSV import could leave its earliest record out of the refresh that follows it.** The time
  that refresh starts from was the earliest added record's, rounded to the second: half a second or
  more past the second, the record fell before it. It is now the second at or before the record.
  Audit records carry whole seconds, so none is known to have been left out.

### Changed
- **`audit.vLicenseSkuChange` reads a table.** Its columns are the same, so what reads it needs no
  change; a second license database made by hand can still be any view of that name. It has one
  row per record, SKU and direction: the old view gave a change twice when a record carried the
  license lists both in its own properties and in its chunks, which no reader counted twice. The
  old view is kept as `audit.vLicenseSkuChangeFromRecords`, the rule the table is checked against.
  In an upgrade the view moves to the table only once the table's rows are there: until the
  installer has worked them out, and if that step fails, it goes on reading the records.
- **`audit.usp_RefreshLicenseSkuChange`** keeps the table, and `audit.LicenseSkuChangeRun` has one
  row per run. With no parameter it works out the events shredded since its last run began, with
  every record of their chunk groups and every record raised with any of those: a change can take
  its lists or its user from such a record, or give way to it, so a family is worked out whole.
  When more than a tenth of the events were shredded since, or with `@All = 1`, it works out every
  event. A run that fails leaves nothing behind: the next takes its events too. The shred tells it
  when it began (`@SinceShreddedUtc`), so its events are taken even if another session's run came
  in between. Only the shred writes license events; after changing them any other way, run it with
  `@All = 1`. While a shred is running or one was stopped part way, a run asked for by hand without
  `@All = 1` works out nothing: that shred's events can still lack their properties.
- **Two marks in the license-event database's `audit.PipelineSetting`.** `LicenseShredInProgress`,
  while a shred runs, holds the time it shreds from (empty: every record); one that is still there
  when no shred is running is a shred that was stopped, and its `UpdatedUtc` stays the time that
  one began, however many shreds stop after it. `LicenseShredCompleteUtc` is when a shred of every
  record last finished, or when the installer was told that one had (`-LicenseEventsShredded`).
- **A new index**, `IX_LicenseEvent_Shredded` on `audit.LicenseEvent (ShreddedUtc)`, finds the
  events shredded since the last run.
- **A SKU added and removed in the same second.** The session build took whichever of the two the
  plan met first. It now takes the addition as the later one, as the `bi` refresh does, so a
  session and its interactions agree.
- **`Install-CopilotAudit.ps1`.** `-Rebuild` also shreds every license record again, now that an
  ordinary run does not. `-LicenseEventsShredded` is new (step 8 above). Where a shred was stopped
  part way, the installer has it finished: what it was shredding is shredded again, and no more.
  And it works out the SKU changes of license events written some other way than by a shred.
- **The demo** installs the license-event procedures and works out its license changes, as it
  writes its license events directly.
- **The schema reference** ([docs/SCHEMA.md](docs/SCHEMA.md)) describes the two new tables and
  the view that keeps the rule, and catches up with earlier releases where it had fallen behind
  the scripts: `audit.CopilotBackfillRange.Source`, `ResourceAction` at 450 characters in
  `bi.DimResourceAction`, the columnstore indexes of `dash.UserDay` and `dash.ResourceDay`, and
  the key of `dash.NonHumanException`.

### Added
- **`tools\Measure-CopilotNightly.ps1`**: where a nightly run's time went. The last runs with each
  step's time; one run taken apart into the Purview download, the merge, the flatten, the shreds,
  the license SKU changes, the sessions, the report tables and the dashboard totals, each with its
  time, its share and what it wrote, from the tables each stage already fills; with `-LogPath`, the
  run's own log lines and the time between them; the statements that ran longest, from Query Store;
  and, during a run, the statement running now. It only reads, with uncommitted reads, so it is
  safe beside the task. Against databases on an earlier release it says that the license history is
  the likely cause.
- **`tools\Trace-CopilotNightly.sql`**, for the database administrator: what the task's connections
  are running at this moment, with waits, blocking and the plan as it runs; and an Extended Events
  session, `CopilotNightly`, that records every statement of the task that runs a second or longer,
  read back by procedure and by statement. In SSMS, or with sqlcmd and `-v Do=now`, `start`, `read`
  or `stop`.
- **A slow nightly run**, in [docs/OPERATIONS.md](docs/OPERATIONS.md#a-slow-nightly-run): the two
  tools, turning Query Store on, the same recording with SQL Server Profiler, and how to read the
  result: which stage or wait means what, and what to do about it.
- **The health check** (`tools\Test-CopilotDataHealth.ps1`) says how many SKU changes are kept and
  when they were last worked out, tells a license shred that is running (it holds the lock) from
  one that was stopped, and asks for the installer when the database predates 2.5.4 or no shred
  has finished a stopped one for a day.
- **Tests.** The SQL integration test checks the kept rows against the old view after a full shred,
  after a night's shred that completes a chunk group and brings the license change an *Update user*
  was raised with, case by case over 35 events for every part of the rule, at a tenth of the events
  and just over it, and through an upgrade from a database with events and no table, with and
  without the step that works them out. It makes a shred fail as it writes a batch's properties (a
  trigger stands in for a task that is ended) and checks the mark it leaves and what the mark says
  is owed, that the kept changes stay as they were though asked for by hand, that a second shred
  that stops leaves the mark's time alone, that the next shred finishes it from where it began,
  that a shred waits while another holds the lock, and that a shred's events are worked out though
  it runs on for seconds after stamping them. Through the installer it checks eleven runs: records
  not shredded yet, nothing to do, `-Rebuild`, a stopped shred finished (of a night, and of every
  record), a database with no mark shredded once more, `-LicenseEventsShredded` taken, not taken
  beside a chunk group never reassembled, a record that arrived since shredded from its own time
  on (alone, and beside a stopped shred), and an event changed by hand. The checks of the mark, its
  time, the lock, the run asked for by hand and the time a shred passes on were each run against a
  copy of the procedure broken in that respect, and failed there. It runs the trace script through
  `start`, `now`, `read` and `stop` against a three-second probe, on SQL Server 2022 in CI, and the
  profile against its own recorded run.
  `tests/Test-NightlyProfile.ps1`, in CI and the release, checks how the profile splits a run into
  stages, how it reads a log, and that it only reads.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- Every time above is the lab's: a database of 120 million interactions (40,000,000 records,
  210,000 people) with a license history made for the test, of 4,000,000 events (2,800,000
  *Update user* records that are not license changes, and 300,000 changes each raised twice and
  split in two), on one workstation with 8 cores and 31 GB, SQL Server 2025 Enterprise Developer.
  The mix of that history is an assumption. No production night was measured on this release: how
  much of a long night these three causes account for on a given tenant is what
  `tools\Measure-CopilotNightly.ps1` shows, before the upgrade and after it.
- The Purview download is as it was: the service sets its pace.
- The shred itself was not timed at that size: the lab's license history was written as events,
  not as records. The one more shred a first install makes without `-LicenseEventsShredded` takes
  what that step took in earlier upgrades.
- `-LicenseEventsShredded` is the operator's word. The installer can see one kind of damage itself
  (a chunk group never reassembled) and then shreds all the same; events a stopped shred left
  without their properties it cannot see, which is why the default is to shred once more.
- A shred that is canceled in a query window keeps its lock while that window stays open, and the
  next shred waits for it, with no limit: its log says *Another shred of the license events is
  running: waiting for it to finish.* Closing the window lets it go. A shred that fails, or whose
  process ends, lets it go itself.
- The health check's lines about a shred's mark, and `tools\Measure-CopilotNightly.ps1`, are tested
  on a computer with Windows authentication only, which CI's SQL Server does not have.
- The licensed days are still rebuilt whole every night (a minute and a half for 22 million in the
  lab), and the dashboard's org chart too.
- Five statements now ask SQL Server to seek an index and do not fall back to a scan: the kept
  changes' events by `ShreddedUtc`, by chunk group and by `CorrelationId`, their properties by
  record, and the resources of a night's interactions. If one of those indexes is dropped by hand,
  the statement fails (error 8622) until `Install-CopilotAudit.ps1` creates it again.
- A full `bi` rebuild and a full session build were not timed again at 120 million interactions.
  Each month of a full `bi` rebuild now seeks its resources as a night does: for one month of the
  lab's (3.4 million interactions, 5.9 million resources) that step took 57 seconds sought against
  5 min 10 s left to the optimizer, with the table in memory; with it on disk it was not timed.
- A session built before this release keeps the license flag it has; the same-second rule applies
  to sessions built from now on, and `-Task RebuildSessions` applies it to all.
- `tools\Trace-CopilotNightly.sql` was run on SQL Server 2025 here and on SQL Server 2022 in CI,
  not on 2017 or 2019. The plan as a statement runs needs 2019 or later, or trace flag 7412.
  `tools\Measure-CopilotNightly.ps1` connects with Windows authentication only, as the health check
  does.
- The SQL Server Profiler steps were written from Microsoft's documentation and were not run.
- As for 2.5.1 to 2.5.3, the installers were not run on Windows Server with IIS for this release.
  The dashboard's code is 2.5.3's; the UI audit was run again at 30 days on the demo, whose license
  history now comes through the kept table.
- The Power BI model is unchanged in this release, so it was not opened in Power BI Desktop for it.

## [2.5.3] - 2026-10-04

Daily trend charts no longer end on days whose records may still be arriving. At 7, 14 and 30 days a
chart is drawn to two days before the last record's day, and says so in a line under it.

To upgrade: install the dashboard package over 2.5.2 or 2.5.1 with this release's
`dashboard\Install-CopilotInsights.ps1`. Nothing in SQL or in the job server's scripts changed since
2.5.1, so a database and a job server on 2.5.1 are ready for it: `Install-CopilotAudit.ps1` need not
run, and nothing is rebuilt. From 2.4.12, first upgrade the databases and the job server as for
2.5.1, with this release's packages, then install this dashboard. No KPI, table, digest or data API
figure changes; only which days the daily charts draw.

### Changed
- **A daily trend stops two days before the last record.** At 7, 14 and 30 days, every trend chart,
  column chart, heatmap and sparkline is drawn to the day two days before the last record's day:
  with records to 4 October, to 2 October. Purview can deliver a record up to 48 hours after the
  event, which is why each nightly load reads the last 48 hours again. Until it has, the two newest
  days may not all be in, so a daily line ended in a drop that was the load and not the use: with a
  load that had reached 07:00 on a Saturday, that day's point was a few hours of a weekend.
  The last record's day is the last day with any record, today's first hours included where the
  load has reached them; where it has not, two days of the period are held back, not one.
- **The chart says so, on the page.** A line under each such chart reads, for example, *Shown to
  2 Oct. Records from the last 48 hours may not all be in yet, so the newest days are left out.* It
  is part of the chart, not behind an information icon, so a chart copied out still carries it. A
  table with a trend in each row (Apps, Agents, Leadership › Organization) has the same line under
  it, and the note behind each chart's information icon says it too.
- **What does not change.** The days held back still count in the KPIs, the tables and the digests,
  which run to the last complete day as before, and the data API's trend still has every day of the
  period. Weekly trends (90 days or more) are as in 2.5.2: whole weeks, each drawn once its Friday
  is loaded.

### Added
- **Tests for it.** The dashboard's tests check that with records to 4 October a daily trend at 7,
  14 and 30 days is drawn to 2 October and keeps every day as a row for the totals; that a load which
  has not reached today holds back two days of the period; that weekly trends are unchanged; and
  that a line chart, a column chart and a heatmap carry the line under them, also when drawn again
  at another width.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- The two days are counted from the last day with any record, not from the hour the load reached. A
  load that reaches only the first hours of a day still makes that day the last record's day.
- Weekly charts are not held back: on a Saturday morning the week to Friday is drawn, though a late
  record for its last day or two can still raise it at the next load.
- Adoption › Drops and spikes and its alert still look at every day up to the last complete day, so
  a day that is only partly loaded can show there as a drop.
- Page speed was not measured again at 120 million interactions. The one query that changed runs on
  every request and now also reads the last day with a record: it took 10 microseconds there.
- As for 2.5.1 and 2.5.2, the dashboard installer was not run on Windows Server with IIS for this
  release; the site ran under Kestrel on a Windows 11 workstation for the UI audit.
- The Power BI model is unchanged in this release, so it was not opened in Power BI Desktop for it.

## [2.5.2] - 2026-10-04

Weekly trend charts no longer end in a drop that is only the calendar. A chart of 90 days or more
draws whole Saturday-to-Friday weeks only: it ends on the last week that is over, not the one under
way, and starts on the first week the period covers in full.

To upgrade: install the dashboard package over 2.5.1 with this release's
`dashboard\Install-CopilotInsights.ps1`. Nothing in SQL or in the job server's scripts changed since
2.5.1, so a database and a job server on 2.5.1 are ready for it: `Install-CopilotAudit.ps1` need not
run, and nothing is rebuilt. From 2.4.12, upgrade the databases and the job server as for 2.5.1,
with this release's packages, then install this dashboard. No KPI, table, digest or data API figure
changes; only which points the weekly charts draw.

### Changed
- **A weekly chart draws whole weeks only.** Periods of 90 days or more trend by Saturday-to-Friday
  week. The last week was drawn while it was under way, and the first when the period started after
  its Saturday. Each counted a few days against the other weeks' seven, so every weekly line ended
  in a drop: on a Saturday, the last point was the people of that one Saturday. A week the period
  covers only in part is now left out of every trend chart, column chart, heatmap and sparkline over
  weeks, with any event marked in it. A weekly chart so starts at the first whole week and ends at
  the last Friday with data, up to six days before the period does. Those days still count in the
  KPIs, the tables and the digests, which cover the whole period as before. The data API's trend
  still has every week of the period, the first and last possibly partial. The note under a weekly
  chart says so. Daily trends (periods up to 30 days) are unchanged.

### Added
- **Tests for it.** The dashboard's tests check that a line, columns, a heatmap and a sparkline over
  weeks draw the whole weeks only, with their events moved to match; that a period marks the weeks
  it covers only in part (the 180 days to Saturday 3 October 2026 start on a Tuesday and end on a
  Saturday); that the trend's rows keep those weeks for the totals; and that a heatmap's column
  labels stay inside the chart.

### Fixed
- **A heatmap's column label no longer runs past the chart's right edge.** A heatmap labels every
  few columns, counting from the first. When the last column was one of them, its label ran past
  the edge: over the last of 25 weeks, "w/e 2 Oct" by 5 pixels. Such a label is now moved in until
  it is whole inside the chart, and left out where that would crowd the label before it.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- A week shows on the weekly charts once it is over: on a Thursday, they end at the Friday before,
  and the days since show only in the KPIs and the tables.
- Page speed was not measured again at 120 million interactions: no query changed, only which of the
  points already read a chart draws.
- As for 2.5.1, the dashboard installer was not run on Windows Server with IIS for this release; the
  site ran under Kestrel on a Windows 11 workstation for the UI audit.
- The Power BI model is unchanged in this release, so it was not opened in Power BI Desktop for it.

## [2.5.1] - 2026-10-04

Trend charts show each day and week as it was again, and every report is ready before the working
day starts. 2.5.0's 28-day moving average is gone from the trend charts. Adoption rate stays the
one rolling figure, as it has been since 2.4.9. Everything else 2.5.0 brought stays. A page from
memory no longer waits 0.4 seconds to count the people in each team, and a new script, scheduled by
the dashboard installer, builds every report page ahead at 07:00 Eastern Time each day.

To upgrade: install the dashboard package over 2.5.0 with this release's
`dashboard\Install-CopilotInsights.ps1`, which copies the pre-cache script into the site and
schedules it. Nothing in SQL changed: `Install-CopilotAudit.ps1` need not run, and nothing is
rebuilt. No KPI, table, digest or data API figure changes; only the points of the trend charts do.

### Changed
- **Trend charts show each day or week as it was, as before 2.5.0.** The 28-day moving average is
  gone from every trend chart, sparkline and heatmap that had it. A point is that day's or that
  week's own count again, a share is that day's or week's own share, and a chart reads only its
  period. The *28-day average* tag, the *(as counted)* lines and the notes about the average went
  with it. A weekly trend's first and last week may be partial again, and say so.
  **Adoption rate is unchanged.** It is still the share of Premium license holders who used Copilot in the 28 days
  up to each point (since 2.4.9), so a weekend or a holiday does not dip it. Its first 28 days of
  data read low.
- **Everything else in 2.5.0 stays:** the filter bar shows only the controls that change a page,
  Admin › Health has no *CSV import* step, Admin › Value names opens on the values seen most, the
  AD export reads the country from `c` and the department from `-DepartmentAttribute`, departments
  hold 200 characters, the read-only health check, the type names on Labeled content by unit and the
  order of the levels on Workforce.

### Added
- **A daily pre-cache.** `dashboard\Invoke-InsightsPrecache.ps1` asks the site on its web server to
  build every report page again, for the whole organization at every period, reading each query
  afresh, so every result is new, and kept for its full time, when the working day starts. Whoever
  opens a page during the build gets the result kept before, so nobody waits. The site then builds
  each page once more from memory and times it. The script waits for the answer, logs it to
  `precache\precache.log` in the site's folder, and exits 0 (built, every page from memory in under
  a second), 2 (built, with the pages that took longer named) or 1 (not built, and why). Admin ›
  Health › *Report speed* shows the last one.
  `Install-CopilotInsights.ps1` copies the script into the site's folder and schedules it as
  *Copilot Insights pre-cache*, every day at 07:00 Eastern Time (EST in winter, EDT in summer)
  whatever zone the server's clock is in, as LOCAL SERVICE. `-PrecacheAt` and `-PrecacheTimeZone`
  change it, `-PrecacheAt ''` removes it, and a later run keeps the time it has. The script asks
  through a file in the site's `precache` folder, which the installer lets the app pool and LOCAL
  SERVICE change, so it needs no sign-in to the dashboard or any role. A build the site is already
  doing gives way to it. Run it by hand to build every page now, after a restart for example.
- **Tests for what this release changed.** `tests/Test-Precache.ps1`, run by CI and the release
  workflow. It checks that the task's triggers start exactly one run a day at 07:00 Eastern on
  every day of a year, from a server whose clock is in Eastern Time, UTC, London, the US west coast
  or Tokyo. It also checks the time format, what the script says and its exit codes, and how the
  installer, the script and the site fit together. The dashboard's tests check that no report page
  averages a trend and that Adoption rate stays a rolling 28 days.

### Fixed
- **A page from memory no longer waits 0.4 seconds for the team list.** Every page's *Look inside a
  team* list shows the parts below the manager shown that reach the minimum group size, counting the
  enabled people in each (2.4.12). That count ran on every request, from memory or not: for the top
  of an organization of 210,000 it took 0.4 seconds a page. It is kept with the report data now,
  under the same data stamp, and counted again only when the org chart is rebuilt; so is the size of
  a part asked for. Who the viewer is and what they may open are still read from the database on
  every request. At 120 million interactions a report page from memory takes 0.01 seconds at the
  median and 0.06 at most, where it took 0.42 and 0.77.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- Measured on the lab's 120-million-interaction database (210,000 people) with SQL Server 2025
  Enterprise Developer (8 cores, MAXDOP 6), the site on the same machine: every report page for the
  whole organization at every period came from memory in 0.06 seconds at most, and a page inside a
  part of 70,000 people in 0.08. Building every page ahead took 10.5 minutes; the pre-cache, with
  other work on the server, 14. On Standard Edition the build takes longer, and so does a page not
  yet built. DEPLOYMENT.md, *What has run in the lab*, has the figures.
- The pre-cache builds the whole organization's pages, which the top of the organization, their
  direct reports and Organization holders see. A page inside a manager's part of the organization,
  and a page with a choice (one app, one agent, a sorted or paged list), is kept from the first time
  someone opens it, as before, and that first visit reads the database. Inside the part of 70,000
  it took 0.62 seconds at the median and 7.7 at most (the Overview at 365 days); a page with a
  choice, up to 5.1 seconds.
- The installer's new step and the scheduled task as LOCAL SERVICE were not run on Windows Server
  with IIS. The script and the site were run together, by hand, on a Windows 11 workstation with
  the site under Kestrel.
- The Power BI model is unchanged in this release, so it was not opened in Power BI Desktop for it.

## [2.5.0] - 2026-10-03

Trends that read as trends, and a dashboard that shows only the controls that matter. Every trend
chart is now a 28-day moving average, so a weekend or a holiday no longer dips a line, and says
so. Admin › Health drops its *CSV import* step, Admin › Value names opens on the values seen most,
and a directory that keeps the department in a custom attribute loads without an edit to the
script. The read-only health check is new, and no longer cries wolf.

To upgrade: replace the scripts, run `Install-CopilotAudit.ps1 -Dashboard` once, and install the
dashboard package over 2.4.12. The installer widens `audit.UserDirectory.Department` to 200
characters (the nightly directory load would too); nothing is rebuilt, and no KPI, table or digest
figure changes. If your directory keeps the department in another attribute, set
`DepartmentAttribute` in `copilot-audit.settings.psd1` first, or pass `-DepartmentAttribute` to
`Install-CopilotJobServer.ps1` (see *Added*).

### Changed
- **Trend charts show a 28-day moving average, so a weekend or a holiday no longer dips a line.**
  Each point is the average of the 28 days up to it: a day and the 27 before it for a period of 30
  days or less, a Saturday-to-Friday week and the three before it for a longer one. A share or a
  rate (the Chat and agent share, the labeled share, sessions with a follow-up, messages per
  prompt, the signal rate by unit) is worked out over the whole window, the numerators over the
  denominators, so a quiet day weighs little and the figure agrees with the period's own. The
  charts read the 27 days (three weeks) before the period too, so the first point has its whole
  window; a week not yet over is left out and its point is blank. It covers the trend charts,
  sparklines and heatmaps of the Overview, Organization, Apps and agents, Board pack, Use cases,
  Funnel and trend, Usage by license, Agents, Apps, Build and publish, Security, Compliance, Models
  and web search, Engagement, License inventory, Demand for licenses, Countries and offices,
  Working patterns, Agent hand-offs, Conversation depth and Copilot controls. Every chart that
  averages says so beside its title, in a *28-day average* tag, and in its note; a table's trend
  column says so in its heading, and a line that is a count at a moment, such as the license holders
  on the Overview, says *(as counted)*. A test fails if a page averages without saying so. Not
  averaged: the licenses held (a count at a moment, which an average would only lag), Adoption rate
  (already a rolling 28 days since 2.4.9), the weekday and hour heatmaps, retention and cohorts, the
  forecast and the license pool. Every figure over the period, the KPIs, the bars and the tables,
  stays the period's own sum, and the data API and the digests keep the days and weeks as measured.
  People per agent keep the minimum group size: a point is shown where the average is at least the
  minimum, which means at least that many different people used it in the window. The first 28 days
  of the data read low, with fewer than 28 days behind them, as Adoption rate does.
- **Admin › Health no longer has a *CSV import* step.** Loading from Purview exports is mostly a
  first-time job, so its row in the Pipeline table, a *Not used* line on nearly every site, is
  gone. *Audit download* stays, and `tools\Test-CopilotDataHealth.ps1` still reads CSV imports.
- **Admin › Value names lists a column's values most seen first.** It opened with the unnamed
  values first, which for a column such as resource types or apps put rare values above the ones
  every report shows. *Unnamed first* is still one choice away in the sort list.
- **A report shows only the filter-bar controls that change it.** Every page now says whether the
  period and the part of the organization (*Look inside a team*) matter to it. The License pool,
  the Group overview and the Person record have no team list, as the tenant's seats, each tenant's
  whole organization and one person do not change with it, nor the *Showing* path beside it; a
  page that follows neither shows no filter bar. Pages with a window of their own already had no
  period box; three said it wrongly and now say what they cover: the Forecast (the last 52 weeks,
  not all history), Retention by license month (the licenses held today) and Retention and habit
  (20 weeks, not 12). A test fails if a page that says a control does not matter uses it.
- **The AD export takes a user's country from `c` when `co` is empty.** It reads `co`, else the
  English name of the two-letter code in `c` (CA is Canada), so a directory that sets only `c`, as
  one provisioned by an identity manager often does, no longer loads every country empty.
- **`audit.UserDirectory.Department` holds 200 characters.** It held 64, AD's limit for
  `department`, and a custom attribute can be longer, which would have failed the whole nightly
  directory load. The next load widens an older table; a department over 200 characters is cut and
  counted in the log.

### Added
- **A read-only health check.** `tools\Test-CopilotDataHealth.ps1` reads both databases and says
  what is loaded and from where (CSV files or API loads, and any gap between them), what is owed
  or behind (flattening, sessions, `bi`, the dashboard), whether the license database holds
  Copilot Studio's operations and has shredded them, and which agents have no maker or owner. It
  ends with what to run, in order. RUNBOOK section 5g says how to read it, and how to rebuild
  everything from the stored records without searching the audit log again.
- **The department can come from another AD attribute.** `tools\Export-UsersWithHierarchy-ToSql.ps1
  -DepartmentAttribute pncUCustom08, department` reads the first attribute with a value, so a
  directory that keeps the department in a custom attribute needs no edit to the script. Set
  `DepartmentAttribute` in `copilot-audit.settings.psd1`, or pass `-DepartmentAttribute` to
  `Install-CopilotJobServer.ps1`, so the nightly run reads the same; a tenant can have its own. The
  load says how many people have a department and a country, and warns when no one has a
  department: AD returns nothing, with no error, for an attribute the account may not read.
- **Tests for what this release changed.** `tests/Test-DataHealth.ps1`, run by CI and the release
  workflow, takes the health check's own functions and closing block from the script and tests
  what it says at the end, how it finds a gap in coverage, that a license record counts with or
  without its trailing period, that its license operations are the pipeline's, and that it only
  reads. The dashboard's tests hold the 28-day average (its windows, its rates, the minimum group
  size, where a trend query starts), that every page that averages says so, the filter bar, the
  Health step, Value names' first sort, Labeled content by unit's type names and the order of the
  levels.

### Fixed
- **The health check no longer warns that a license database holds "other operations" when it
  holds license events.** Entra records an operation with a trailing period (`Update user.`),
  which `audit.usp_ShredLicenseEvents` strips and the check did not, so every license record
  counted as another operation and the warning asked whether `-LicenseDatabase` was the right
  database. It strips the period the same way now. A record that is neither a license nor a
  Copilot Studio operation still counts as other, and no longer also counts as a license record.
- **The health check no longer says every check passed when it did not look at everything.** Its
  closing line printed whenever no command was to run, even after a section stopped, after the
  license database could not be opened, or after warnings. It now says how many checks did not run
  to the end, or how many warnings need a look, and says every check passed only when none did.
- **The health check no longer says it takes no lock.** It reads uncommitted data, so it takes no
  row or page locks, but it still holds a schema-stability lock while a query runs: a query on a
  table the nightly run is truncating waits for it. The script's description and RUNBOOK section 5g
  say so. Two unused variables are gone.
- **Labeled content by unit names resource types as the other pages do.** *Resources read, by type
  and label* showed each type as the audit log records it (`CITATION`,
  `http://schema.skype.com/HyperLink`) even after Admin › Value names gave it a name, while Apps,
  Usage by license and Compliance showed the name. It shows the name too now, and its note says
  where type and label names are set.
- **Workforce › By level in the organization lists the levels in order.** It sorted the names as
  text, so Level 10 and Level 11 came before Level 2. Levels are in numeric order now, 2 to 9 and
  then 10, 11, 12; a name that is no level follows the last, and the combined bar of smaller groups
  stays at the end.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- A trend reads the days before its period, 27 or three weeks, so its first point has its whole
  28-day window: a 30-day period reads 57 days of the trend's tables, a 7-day period 34, and a
  365-day period about 6% more. This was tried on the demo database (180,000 interactions), not
  at 40 or 120 million, where the page times in DEPLOYMENT.md, *What has run in the lab*, were
  measured on 2.4.11. If a trend page is slower than it was on a large install, the period it was
  slow at is the thing to report.
- The Power BI model is unchanged in this release, so it was not opened in Power BI Desktop for it.
  Its charts are not moving averages: only the dashboard's trends are.

## [2.4.12] - 2026-10-03

Every finding of an adversarial review of 2.4.11, 107 of them: the privacy rules hold on every
page, digest, extract and API call; a re-run can no longer empty the audit table or count
interactions twice; a slow mail server can no longer stop the site; the license scripts carry
only plain sign-ins; only one run works on a database at a time; refreshes that fail are made
up; several tenants and several web servers each act on their own; and the installers keep
what they are not given again. Two 2.4.11 regressions are put right. A second review, of these
fixes, found 26 more, every one fixed but two noted under *Known limits*: no part of the
organization, chart, digest or Power BI view now rests on fewer people than the minimum.

To upgrade:

1. Disable the nightly task. Before you start, check Admin › Directory quality › *No SID
   recorded*: from 2.4.12 the people it lists cannot sign in until their SID is loaded or their
   account is mapped in `Insights:IdentityMap` (see *Changed*).
2. Back up the databases, replace the scripts, and run `Install-CopilotAudit.ps1 -Dashboard`
   once, adding `-ReportingAccount` with the Power BI gateway's account so it can read the `bi`
   views and no longer the tables under them. With several tenants, run it for each tenant's
   databases too (OPERATIONS.md, *Upgrades and renewals*). The installer counts once that every
   record has been flattened (minutes at 40 million records), and reads the agent ids of other
   apps' records once; only when some gain one does it rebuild `bi` and the rollups.
3. Run `.\tools\Invoke-CopilotMaintenance.ps1 -SqlServer SQLPROD01 -Database CopilotAudit -Task
   RefreshDashboard` once for each tenant's database (its `-Database`), so the rollups of labeled
   content and blocked sites count interactions, not resources.
4. Install the dashboard package over 2.4.11. A re-run now keeps what it is not given, so
   `-PackagePath` and `-SqlServer` are enough. With more than one web server, set
   `Insights:DataProtectionKeysPath` to a share first (DASHBOARD.md, *Several web servers*), and
   point scheduled extracts at a share.
5. Take the new Power BI model into yours (it carries its refresh policy and an Organization
   role that now covers every table). Don't publish the copy in `powerbi\`: it points at
   `localhost` and has none of your pages or measures. Write the release's model beside your
   working copy with `python powerbi\generate_model.py --out D:\PowerBI\CopilotInsights-new`,
   bring the changes across in Power BI Desktop (DEPLOYMENT.md, step 15; OPERATIONS.md, *A new
   Power BI model*), publish your working copy, then refresh it in full (DEPLOYMENT.md, step 16,
   item 4). Desktop loads the whole history before it publishes, since `RangeStart` and
   `RangeEnd` span every date: on a large database, narrow them first.
6. Enable the nightly task.

Two new columns (`audit.CopilotLoad.DerivedRefreshedUtc`, `audit.CopilotCsvImport.DerivedRefreshedUtc`)
and a database role for reporting accounts (`CopilotReporting`), all added by the installer.

### Fixed
- **A report can no longer be narrowed to one person.** Any sign-in in `?org=` was taken, so
  every page without a role, and `/api/v1/usage`, became a named person's use, hours of the day
  and, on the board pack, their security signals. A part of the organization other than the
  viewer's own is now shown only when it holds at least the minimum group size; the "Look
  inside a team" list offers only those; a page asked for a smaller one shows the viewer's own
  part with a note; the data API refuses it, and a digest set up for it is not sent.
- **"Smaller groups" is never one person.** Every breakdown folded the groups under the minimum
  into one row without checking that row reached it: a manager's own direct unit, or a single
  small department, came out under that name, the head of the organization among them. Lists
  that left small groups out beside a total gave them back by subtraction. One rule now serves
  every page, digest, chart and API call: when the small groups together are under the minimum,
  the smallest groups shown join them.
- **Breakdowns that skipped the minimum.** `/api/v1/value` returned every department, down to one
  person; it folds them as the page does. Workforce showed a one-person Managers group, and its
  job-title chart used 15 as the minimum. The people who used one agent are counted only at the
  minimum or more on every page (Agents in use, Agent detail, Agents costing credits, Apps and
  agents, Agent hand-offs) and in the weekly app extract, as Agent inventory already did.
- **License action outcomes keep to the viewer's scope.** The page and its CSV read every export
  ever made, so a manager with the Licensing role saw whom anyone had exported, organization-wide.
- **Privacy mode covers the named lists on pages without a role.** Demand for licenses, Agent
  detail, Who made the changes and the people who worked with labeled content named people to a
  role holder with privacy mode on; a personal OneDrive's address, which names its owner, is no
  longer shown either. A test now finds any role check on such a list that does not also check
  privacy mode.
- **Privacy mode no longer names managers.** Units are named after their managers: in privacy
  mode they go by the manager's department, are not links, and nobody can open a manager's part
  of the organization by name; `/api/v1/units` leaves out their sign-ins; a digest for a
  manager's part is not sent, and a target for one is shown by its department.
- **Privacy mode fails closed.** A failed read of the setting (a failover, a missing grant) was
  taken as "off" for a minute, on that server, for pages and emails alike. Only a database from
  before the setting is off now; any other failure is on, logged, and read again in seconds.
- **`/api/v1/usage` and `/api/v1/units` keep to the section roles.** They gave the jailbreak,
  injection and blocked counts that `/api/v1/security` refuses without the Security role, and
  the labeled-content counts of the Compliance section. Those fields are now empty without the
  role. DASHBOARD.md's table of endpoints names the roles the code requires.
- **Finalize no longer empties `audit.CopilotAudit`.** Its guards read only the window table, so a
  re-run after staging was dropped (as finalize itself advises) or purged dropped the audit table
  and rebuilt it from nothing, saying "Coverage verified". It now refuses unless staging holds
  every row the windows loaded, leaves a mark when it finishes, and a backfill run again over a
  completed range leaves finalized tables alone, so `-History` carries on past them after a
  later step failed, or after the nightly has merged a load.
- **A mail server that does not answer no longer stops the site.** A timeout is an
  `OperationCanceledException`, which every catch on the mail path let through as if the site
  were stopping, so the background service failed and .NET stopped the site, again after each
  restart. A timeout is now a failed send, logged and retried; the Exchange Online token request
  waits 30 seconds, not 100; and a background service that fails is logged without stopping the
  site.
- **The license scripts carry only plain sign-ins.** PowerShell also ends a quoted string at the
  curly quotes, and Windows PowerShell 5.1 reads a file without a byte order mark as
  Windows-1252, where some letters' bytes are curly quotes: a sign-in from a directory that does
  not check them could run commands on the administrator's machine. A script now takes only
  sign-ins in the characters Entra allows (the rest are left out, and it says how many; the bulk
  file lists them), its group and SKU ids only letters, digits and hyphens, and every script
  file starts with a byte order mark.
- **Re-running the job-server setup keeps its settings.** It kept only its own keys, and only
  values that were not empty: `EmployeeTypes = @()` went back to EMP and NONW, `IncludeLeaveDates`
  was dropped, a blank license database became `CopilotLicenseEvents`, and a task moved with `-At`
  went back to 02:00. Every saved setting is kept now, empty ones too, `-EmployeeTypes @()` can
  be given, and the task's time is saved.
- **A first flatten stopped part way is finished.** The installer took any flattened row as "built",
  so a first flatten that stopped, or a nightly that flattened only its own days, left the rest of
  history unflattened for good. A flatten of every record now leaves a mark when it finishes; a
  database without one is counted once, given the mark when every record is flattened, and
  flattened and built in full when some are not.
- **Maintenance reads the right license history.** `Invoke-CopilotMaintenance.ps1` defaulted to
  `CopilotLicenseEvents`, the first tenant's, so another tenant's rebuild read that tenant's
  license history, and its nightly then failed every night. With no `-LicenseEventDatabases` it
  now reads what the database was last built with, and says which.
- **Agents › Build and publish keeps an agent's alias and owner** (a 2.4.11 regression). It found
  the agent by its name, which was its schema name until 2.4.11 named agents as the log does; it
  now finds it by its id, and shows the log's name where no alias is set.
- **Agents named only by `AgentId` keep their history** (a 2.4.11 regression). Another app's records
  that carry an agent's id (Copilot Chat running a declarative agent) are keyed on it, which 2.4.11
  read only from its upgrade night on. The installer reads those records once, from the apps that
  carry one, and rebuilds `bi` when any gain an id.
- **`Install-CopilotAudit.ps1` runs in Windows PowerShell 5.1** without `-ReportingTimeZone`: it
  stopped at a .NET 6 property, before any rebuild ran.
- A digest that may not be sent as set up says why, in its status, Send now and the preview.

- **Targets keep to the part of the organization a report covers.** Only Adoption › Targets
  filtered them. The Board pack, `/api/v1/targets` and a manager's Adoption digest listed every
  target, the whole organization's and other executives' parts, to people who may see neither.
  One rule now serves all four. A target for a part smaller than the minimum group size shows no
  figures, except to that manager.
- **The Board pack's seats bought and assigned are for those License pool shows them to.** Every
  viewer of the board pack, scoped or outside the directory, saw the tenant's pool. It is now
  shown to the whole-organization view and the Licensing role, marked as the whole tenant's. A
  manager's Licensing digest leaves the pool out too.
- **License pool and Budget and renewal are the tenant's in every figure.** For a viewer with
  the Licensing role and their own part only, they set that part's use, revocations and upgrades
  against the tenant's seats: 36 used of 1,269 assigned, and a renewal figure that was neither.
- **The period before is compared only where the data covers it.** A 180-day period on 182 days
  of data was set against the two days before it, so the Scorecard and the Board pack read
  "+3997%". The Scorecard, the Board pack, the Leaderboard and the digests now say there is no
  full period before, and when the data starts.
- **The license cost of a period is the licenses held each day.** It was today's licenses times
  every day of the period, so a rollout, or a period reaching back before the licenses, was
  charged in full. On the demo data the return on license cost fell from 3.6× to 1.1× between
  180 and 365 days, on the same value.
- **A month is scaled from the days the data covers.** Better with a license, Better on
  pay-as-you-go and Agents costing credits divided by the period's length, so a 365-day period
  over 182 days of data halved everyone's credits a month and dropped people from the lists.
  Engagement counted working days from 1 January of the first year with data.
- **Licenses on disabled accounts exports the part shown.** Its file and script were built from
  the bare address. A division's 5 accounts on the page became the organization's 27 in the file
  and in License action outcomes.
- **License exports are buttons, not links.** Each export records the people it names and writes
  an audit entry, and it was a GET. A link opened by a Licensing role holder could record an
  export in their name. The exports now post with the page's anti-forgery token.
- **An export is recorded whole or not made.** The record was written a thousand people at a
  time and any database error was ignored, so a timeout gave out the whole file with part of it
  recorded, or none. It is now one statement: if it fails, the download fails and can be tried
  again. Only a database without the table goes on without the record. A repeat of the same
  export that day is recorded once.
- **The Leadership and manager digests rank adoption as the Overview does.** They applied the
  minimum to a unit's people, not its license holders, and sent a team of twelve with one
  license holder as "12 | 1 | 100%". Units under the minimum of license holders are now one
  combined row.
- **The data API's list of endpoints keeps to the API's rules.** `/api/v1` answered with the API
  turned off and to anyone outside the dashboard's levels, and was not in the audit log.
- **Headline figures no longer drop to 0 on an empty page.** Better with a license, Better on
  pay-as-you-go and Agents costing credits read their totals from the page's first row, so a
  search that matched nobody, or a stale page number, showed 0. Upgrade, train or revoke lost
  its count and its files the same way. The pager no longer reads "4,999,951–10,568 of 10,568",
  and Previous leads back to the last page.
- **CSV exports carry what the page shows.** A cell the page builds as formatted text exported
  empty, so Flagged interactions came without their signals, Agents costing credits without the
  agent's name, and Budget and renewal without the days to renewal. Each now exports its text; a
  column of buttons still exports nothing.
- **Negative figures in a CSV are numbers.** The formula guard put an apostrophe before every
  negative value, so a spreadsheet read '-28.68 as text and left it out of its totals. Numbers
  in page exports and scheduled extracts are now written as numbers; text that would start a
  formula is still guarded.
- **What is not in the audit log is not served.** A failed audit write was logged and the page,
  file or API answer went out anyway. A full log or disk, a read-only database or a missing
  grant removed the trail silently, and an org= value longer than its column did it on demand.
  Values are now cut to their columns, a failed write is tried again, and the request is then
  answered 503 with the reason. On the data API, an org= naming no one in the directory is
  nobody.
- **Several web servers can share their form keys.** Each server kept its own keys, so behind a
  load balancer a form drawn by one server and posted to another was refused (HTTP 400), and the
  message after a save was lost. Insights:DataProtectionKeysPath names a share for every
  server's keys, protected for the site's domain account or with
  Insights:DataProtectionCertificate. A site that cannot protect them does not start, and its
  log says why. DASHBOARD.md has a new section, Several web servers.
- **A huge page number no longer breaks a list.** From p=42949674 the offset overflowed into a
  negative OFFSET, and every paged list answered 500, unaudited. The page number is now capped.
- **Long non-human rule lists are refused before anything is saved.** The form accepted 1,000
  characters for a column that holds 400, so a long list failed in SQL Server after the boxes
  before it were saved, with no audit row. Each list is now held to 400 characters, with a clear
  message.
- **Group overview reads grants from the database.** It settled each tenant's viewer from cached
  grants, so a revoked Organization grant kept showing that tenant's whole organization for up
  to 12 hours.
- **The report cache is bounded.** Every new search or page number stayed in memory for 12
  hours, and each data change left the whole previous set behind. The cache now keeps at most
  Insights:QueryCacheEntries results (20,000) and drops a tenant's results as soon as its data
  changes.
- **Cached pages follow the whole license read.** Only the license pool was watched, and it is
  written first, so pages cached during the read kept yesterday's licenses and service-account
  groups when no refresh followed. Every table the read writes now counts.
- **The pages are built ahead when they need it.** A pipeline run's first row threw away every
  cached page and started a build before any data had changed; a run now counts when it ends.
  And nothing was built again when the night's results expired 12 hours later; the pages are now
  rebuilt then too.
- **Admin > Health describes the cache as it is.** It still said the lists edited all day always
  read the database; since 2.4.11 they are kept under a version of their own.
- **The last complete day is cut in the reporting time zone.** It used SQL Server's own date, so
  with the server on UTC and days in Eastern Time, a day still loading counted as complete from
  8 p.m. Every period ended on it, and the security and drop alerts judged it once and never
  again.
- **Settings apply on every web server from the next page.** Privacy mode, the minimum group
  size, Reports shown and the level limit were kept for a minute on each server, and a save
  cleared them only where it was made. They are now read once per page; digests and alerts still
  read them at most a minute old.
- **Several web servers are documented.** Each server keeps its own report cache and builds its
  own pages. The guide now says so, and recommends session affinity and a shared key folder.
- **A follow-up says what it counts.** Sessions with a follow-up were explained as the person
  replying to Copilot, but they count any two prompts less than an hour apart on one day, in any
  app. Engagement and Adoption now say that.
- **A form acts on the tenant its page showed.** With several tenants, a post went to whichever
  tenant the switcher's cookie named when it was sent, so with another tenant chosen in a second
  tab, Remove on one tenant's Admin › Access removed the other's grant with that number. A form
  now names its page's tenant, a post never changes the switcher, and a post that names no
  tenant changes nothing.
- **Run the checks for every tenant runs only where you are an administrator.** An administrator
  of one tenant ran the round in every tenant and read back their results; each other tenant now
  has the run in its own audit log.
- **A digest preview needs the digest's role and is audited.** Any administrator could preview
  the Security, Licensing or Adoption digest without that section's role, and nothing was
  recorded. Preview and Send now need the role the section's pages need, and every preview is in
  the audit log.
- **Stored secrets can be entered again after a restore to another instance.** The database
  master key's password was thrown away, so on another SQL Server instance the secrets could be
  neither read nor stored again, and re-running the installer kept the keys that could not be
  opened; saving ended on the error page. The installer now makes the keys again and removes the
  secrets it cannot read, and a secret SQL Server cannot store is refused in words.
- **Schedules on the days the clock changes.** A digest or extract set in the hour the clock
  skips threw, which stopped every check after it until the next day (a week for a weekly one),
  and one in the hour it repeats was sent about 13 times. The skipped hour now goes when the
  clock reaches the other side, and the repeated hour goes once, the first time round.
- **A stored password goes only where it was entered for.** An administrator could point Admin ›
  Email at a server of their own, leave the password empty, and have the site sign in there with
  the stored mailbox or relay password, in clear text with security None. The password is now
  sent only to the server, port, security and user name it was typed in with; change any of them
  and type it again. Email saves are audited with old and new values.
- **Alerts and digests are marked sent before they go.** They were marked after the email, so a
  database that could be read but not written (a full log) sent the same digest or alert every
  five minutes, and two web servers whose lock had slipped could both send it. A webhook log
  that cannot be written no longer has the alert sent again.
- **An alert email that fails is tried again.** It was marked raised whatever happened, so a few
  minutes of mail-server trouble lost that day's security or pipeline-failure email for good.
  With no webhook to take it either, it is emailed again an hour later, three tries in all.
- **A manager digest cut off by a restart is sent later.** A stop during a manager's digest left
  it "being sent" for good, and that manager never got that period's digest, with nothing to say
  so. The claim is given back, and a message on its way is no longer cancelled part-way.
- **The Exchange Online token answers only its own secret.** It was kept under the tenant and
  app id alone, so an administrator of another dashboard tenant who typed in those ids with any
  secret was handed it, and a mistyped new secret passed Send a test for an hour.
- **Teams alert cards show text as text.** A card read its lines as Markdown, so an agent named
  `[Approve](https://…)` became a link in the alert channel.
- **Value names keeps a name with a comma or semicolon whole.** A mapping file saved again in
  Excel lost the quotes around such a name, and the upload cut it there and stored its tail as
  the note. A line with more cells than it should have is now refused.
- **The audit log is kept at least a year.** An administrator could set its retention to one day
  and apply it, erasing the record of their own actions. It is 0 (keep) or at least 365 days,
  and the change is audited with its old and new values.
- **Scheduled extracts are planned for their own dates,** as every other read between dates has
  been since 2.4.11.
- **Value names no longer claims agent ids name agents.** Since 2.4.11 an agent is known by its
  app identity, so a name given to an agent id reached no page; agents are named in Admin ›
  Reference data.
- **Extracts and several web servers.** A round runs on whichever web server holds the lock, so
  an extract to a local folder was split between the servers' disks while the page said each was
  written. It now fails, saying why, once another server runs the checks, and its result names
  the server.
- **Two `bi` refreshes no longer run at once.** A full rebuild commits a month at a time and
  holds nothing on the fact between months. A refresh that started meanwhile (the nightly run
  during an upgrade's rebuild, say) found its mark, emptied the fact under it, and filled it
  alongside it, so every month after that point was counted twice in `bi`, the rollups and Power
  BI. A second refresh now waits for the first to finish.
- **The rollups follow a full `bi` rebuild.** The dashboard rolled up only the window it was
  asked for, so after a full rebuild finished by the nightly run, or an upgrade whose last step
  was stopped, every older day kept the figures of the old facts for good. Among those figures
  were 2.4.0's Premium rule and the revoke list built on it. The first rollup after a full
  rebuild now covers every day. While a full rebuild has not finished, a dashboard refresh
  leaves the rollups as they are, instead of rolling up a half-written fact and losing the
  months it had not reached.
- **Licenses on renamed and deleted accounts end.** The license history knows a person by the
  sign-in of the time, so an account the directory no longer finds stayed licensed every day to
  today. That made it a reclaim candidate on an account that no longer exists, and after a
  rename the same person was licensed twice. Its licensed days now end with its last use. A user
  the directory finds licensed, whose history leaves nothing held (another Copilot SKU added and
  removed), is now licensed.
- **`-DeleteRows` removes the records altogether.** The purge deleted the records but kept each
  one's shredded row (user, app, thread, time), and the maintenance task kept the flattened row
  too, with the resources and contexts it read (file names, sites, links). They now go with the
  record. `bi` and the rollups keep theirs until `RebuildBi`.
- **A database in another collation works.** The flatten, the license shred and the `bi` refresh
  failed at once with a collation conflict on a database whose collation differs from the
  server's, such as one a DBA created or restored elsewhere.
- **An interrupted read of agent names reads its last batch again.** Stopped in a batch's
  catalog step, the read kept the records' names but not their agents, and the next run skipped
  them, so those agents kept the name read from their id.
- **Compliance counts interactions.** The label and site charts, the Access page's
  labeled-interaction and times-blocked columns, and the label alert counted each resource read.
  An answer drawn from six files with one label counted six, against one in the page's own count
  of labeled interactions. Rebuild the rollups once after upgrading (`-Task
  RefreshDashboard`, upgrade step 3).
- **Power BI honors every Organization grant the dashboard does.** A grant to `DOMAIN\name`
  recorded as a user (a person, or a group `GrantRole` could not look up) gave the whole
  organization on the dashboard but only the viewer's own people in Power BI. The nightly
  directory load now records who each such grant is.
- **A full re-flatten no longer blanks purged records.** `usp_FlattenCopilotAudit @RebuildAll =
  1` emptied the tables first, so every record whose JSON had been purged came back blank. It is
  now refused once any record's JSON has been cleared; the default re-flatten keeps them.
- **A refresh that fails after the nightly merge is made up.** A load was marked merged before
  the flattened table, sessions, `bi` and the dashboard were refreshed, and each run refreshed
  from its own load's start only. A failure that lasted past the 48-hour lookback (three nights
  of *different settings*, or one long catch-up load), or a re-run that found nothing new to
  load, left those days out of `bi` and the dashboard for good, security flags included, while
  the pipeline reported success. Each load now records when its refresh finished, and the next
  run refreshes from the earliest load still owed, even with nothing new to load.
- **A CSV import's refresh is never lost.** An import whose refresh failed or was skipped with
  `-SkipPostProcessing` was never refreshed: a second run skipped the merged files, and the
  nightly refreshes only its own days. Running the import again, or the next nightly, now makes
  it up from the import's earliest record. Left out, `-SessionLicenseDatabase` now reads the
  license databases of the last session build; it read none, so a database built with license
  history refused the session build after the merge.
- **A history backfill with failed windows fails the run.** It logged the failed windows and
  exited 0, so `-History` carried on with the range neither complete nor finalized and reported
  the history loaded and built; the nightly run then failed at *License events* every night. It
  now fails, so the pipeline stops at that step; running it again retries only the failed
  windows.
- **Two merges at once no longer store records twice.** The nightly merge and a CSV import each
  looked for records already loaded and then appended the rest with no lock between, so two at
  once (a console run beside the nightly, or an import beside it) could append the same records
  twice, after which every flatten over those days failed. The merges now take turns under one
  lock.
- **Several license databases work with the nightly run.** The session build compared the
  license list as text, so the same databases in another order or spacing were *different
  settings*, and the nightly could pass only one database: a database built with several, as the
  installer's example shows, failed every night. The list is compared as a set, and the new
  `SessionLicenseDatabases` setting gives the nightly run the list.
- **The session export's `-Build` no longer stops the nightly run.** It rebuilt the production
  sessions under its own defaults (the User rule, no license history), after which every nightly
  session build was refused. It now builds with the last build's settings and refuses others
  without `-Force`.
- **Sessions build on a database whose collation is not tempdb's.** One temp table took tempdb's
  collation, so every session build failed there with a collation conflict.
- **Daylight-saving changes no longer join, split or reorder sessions.** Prompts were ordered,
  and the 60-minute gap measured, on local clock time: in the repeated autumn hour prompts 110
  minutes apart counted as one session and a later prompt could start it, and in spring a
  10-minute gap split one. Order and gap are now in UTC and the day is still cut in the
  reporting time zone. Only sessions in the hours around a change come out differently; `-Task
  RebuildSessions` applies it to sessions built before.
- **A license taken back is recorded.** The license tool looked up only the people in the audit
  data and today's license holders, so someone who never used Copilot stayed *Licensed* after
  their license was reclaimed, and a leaver stayed *Found*. Everyone recorded as licensed is now
  looked up again each night.
- **A manager without a UPN no longer lifts their people a level.** The AD load reported them to
  the manager above, and levels came from that chart alone, so the people under such a manager
  right below the top were level 2 and saw the whole organization. `OrgDepth` now counts every
  manager above, and the dashboard's levels take the deeper of the two.
- **The Power BI account reads the `bi` views and nothing else.** `-ReportingAccount` was given
  SELECT on the whole `bi` schema, tables included, so privacy mode's pseudonyms could be
  matched to `bi.DimUser`'s names. It now joins the role `CopilotReporting`, which each run
  grants every `bi` view to; give the account to `-ReportingAccount` once more to narrow it.
- **Re-running the dashboard installer keeps the site's settings.** A run without every
  first-run parameter put the app pool back on its own identity, the connection string back to
  `CopilotAudit` without `TrustServerCertificate`, turned Kerberos off and added HTTP on port
  80. What is not given again is now kept, the bindings included.
- **A failed dashboard install no longer leaves the site offline.** `app_offline.htm` was
  removed only by the last step; it is now removed whatever happens, and the certificate,
  `-Tenant` and the settings file are checked before the site goes offline.
- **The job-server setup no longer switches to a certificate no app has.** Any run within 30
  days of expiry made a new certificate and put it in the settings, uploaded nowhere without
  `-RegisterApp`. A new one is now made only with `-RegisterApp`, which uploads it to every
  tenant's app, not the first alone.
- **The app registration is not taken on its name alone.** An app someone else had named
  "Copilot audit pipeline" was given admin consent to the whole audit log. The setup now finds
  the app by the client id in its settings; by name it takes only the one app of that name with
  no secret and no owner but you.
- **One run at a time on a database.** Nothing kept the nightly, a run by hand, the installer
  and the maintenance rebuilds apart, so two could load the same windows twice or rebuild `bi`
  under each other and count interactions twice. Each now holds the database while it works, and
  one started beside another stops at once and changes nothing; two downloads into one window
  table are refused the same way. Disable the nightly task before an upgrade, a rebuild or a
  `-History` load.
- **A window split is written whole.** It was three writes, and a run stopped between them left
  a hole that merge or finalize refused for good. It is now one transaction, and a split an
  earlier version left part way is finished when the load resumes.
- **A page that stalls mid-download no longer hangs the run.** HttpClient's timeout ended with
  the headers, so a connection that went silent was awaited until Task Scheduler stopped the
  task 20 hours later. Each page now has its own time limit, and is fetched again.
- **One tenant that cannot run no longer stops the others.** An unreachable tenant database
  ended the nightly run, and a database with no history yet threw instead of saying to load it.
  That tenant now fails alone and the next one runs.
- **Step 7 loads the license history the nightly run reads.** DEPLOYMENT step 7 named four
  license operations, where the pipeline loads eighteen, and the loader refuses to mix them, so
  following it left Agents › Build and publish without history.
- **The upgrade upgrades every tenant.** The guides ran the installer once, which upgrades the
  first tenant's databases alone, and nothing upgraded the others, whose nightly runs went on
  with the procedures they had. They now loop over every tenant in the settings, and read each
  one's license databases as the nightly run does: a tenant without its own `LicenseDatabase` or
  `SessionLicenseDatabases` takes the top-level one, and a blank license database is skipped
  (`-SkipLicenseDatabase`).
- **The Power BI role covers every table a member can read.** The Organization role filtered
  only Users, so Sites, Agents, the organization's security figures and the standalone tables
  were readable whole by every member. Each now follows the role, a viewer who sees everyone is
  marked in `bi.OrgSecurity`, and a Departments role serves department figures alone.
- **`generate_model.py` no longer overwrites a model someone has worked on.** It deleted the
  report pages and every change saved in the project, and the guides named it as the upgrade
  step. It now writes only a project it wrote itself, and the guides say how to take a release's
  model into one you have changed.
- **Licensed users and adoption in Power BI take one period end for every row.** Each group took
  the end of the period that was its own last date, so anyone whose license was removed
  mid-period still counted as licensed.
- **The model carries its refresh policy.** Incremental refresh shipped as a fixed 2025 to 2027
  window with no policy, so history rewritten in SQL never reached the archived partitions. The
  policy is in the model, and the guides say when a full refresh is needed (a rebuild, or an
  app's category changed).
- **A catch-up run is followed by a full refresh of the model.** The model refreshes the last 4
  days, so a night that caught up two or more missed nights (a load refreshes from 48 hours
  before where the data ended), or made up an owed refresh from further back, rewrote days that
  never reached Power BI. The guides now say to refresh the model in full after such a run, and
  the installer says so where its agent-id rebuild rewrote every day; it said "once".
- **What a full refresh is, said once.** *Refresh now* and the scheduled refresh apply the
  refresh policy, so a full refresh asked for by the guides and done that way reloaded 4 days
  only. DEPLOYMENT step 16 now says how: a
  refresh with `applyRefreshPolicy` false on Premium, Premium Per User or Fabric, or the model
  published again on Pro; the other guides point there.
- **The upgrade publishes your own model.** Upgrade step 5 said to publish the model from
  `powerbi\`, which points at `localhost` and has none of your pages or measures. It now takes
  the release's model into your working copy, as DEPLOYMENT and OPERATIONS do, and warns that
  Desktop loads the whole history before it publishes. Step 3 spells out the maintenance
  command, run for each tenant's database, and DEPLOYMENT.md is written for 2.4.12.
- **The release workflow runs with least privilege.** Every job had a write token and actions
  were pinned by movable tags, and a gallery module was bundled with no check. Tokens are
  read-only but for publishing, actions are pinned by commit, and module signatures are checked.
- **The Power BI security test can fail.** It passed on an empty or over-wide result, and its
  manager saw everyone. It now uses a manager below the top two levels with exact counts, and
  the model is checked in CI.
- **Stale statements corrected:** days are cut in the reporting time zone set at install, not
  Eastern time at the top of the finalize script, and `bi.OrgSecurity` does resolve group
  grants.
- **Admin › Value names, Directory and the guides** say what the dashboard does now;
  DASHBOARD.md's table of API endpoints names the roles they need.
- **A part of the organization is sized by the people still in it.** The check on a requested
  part and the "Look inside a team" list counted disabled accounts, so a part of three people
  and several leavers was shown on its own. Both now count only enabled people, as every other
  minimum does.
- **Agent people counts stay above the minimum on charts too.** The per-agent trend, "People
  using the top agents", Agent detail's "People using it" and its By department chart drew
  counts of one or two people, including a "Smaller departments" bar that was itself under the
  minimum. Each point under the minimum is now a gap, and a combined row under it is left out.
- **The board pack's risk signals follow the roles.** Jailbreak, injection and blocked totals
  were shown to every viewer, and labeled content too. They now need the Security role and the
  Compliance role respectively, as on the data API.
- **Adoption is shown only where license holders reach the minimum.** The All departments and
  Organization units tables, the job title, job family, country and office tables, Workforce's
  adoption figures and its By level chart showed a rate for one or two license holders, and so
  did an adoption chart's combined bar when everything was small, and the digest's unit table.
  These are now blank or left out below the minimum.
- **The leaderboard compares only like with like.** "Change (points)" and "Most improved" set a
  group against the period before even when it was then under the minimum. Now they compare only
  when it met the minimum in both periods.
- **People without a job family are not counted when they are folded.** The KPI showed the count
  that the "Smaller groups" fold was hiding. It now shows only when "(no family)" is a row of
  its own.
- **An administrator without the role cannot add or resume a digest.** Only Send now and Preview
  were checked, so a Security digest could be added and resumed without the Security role.
  Adding and resuming are now refused the same way.
- **Manager digests skip parts too small to report on.** Each was refused, marked failed,
  retried three times and reported as "not sent (see Admin > Email)". Such managers are now left
  out, and a refusal is settled as skipped.
- **A digest for someone who has left the directory is refused.** It went out as "(nobody)" with
  zeros and was reported as sent. It is now not sent, with the reason given.
- **Department figures no longer show whether one person used Copilot.** `bi.DepartmentWeek` had
  a row only where someone was active, so a hidden row for a one-person department showed
  whether that person used Copilot that week. Now every department has a row for every week of
  the data, departments smaller than the minimum are combined as *(smaller departments)*, and a
  week with fewer active people than the minimum is blank.
- **The audit log is kept at least a year, whatever is stored.** Only the admin form enforced
  the 365-day floor, so a shorter value set in SQL deleted the log sooner. The nightly retention
  now treats 1 to 364 days as 365.
- **Reporting accounts can read the dashboard's `bi` views.** The installer granted
  `CopilotReporting` only the views that existed when it ran, so `OrgSecurity`, `DepartmentWeek`
  and the other views the dashboard installer creates later were unreadable.
  `Create-CopilotOperationsSchema.sql` now grants every `bi` view to the role.
- **The job server installer passes the license databases the nightly uses.** A saved blank
  `LicenseDatabase` made `Install-CopilotAudit.ps1` run `CREATE DATABASE []` and fail, and
  `SessionLicenseDatabases` was not passed at all, so every nightly then refused the build's
  different settings. Now a blank value skips the license database, and the list is passed as
  `-LicenseEventDatabases`.
- **Loads and CSV imports run by hand wait their turn.** `Invoke-CopilotAuditBackfill.ps1` and
  `Import-CopilotAuditCsv.ps1` took no run lock, so they could load beside the nightly and two
  imports could empty each other's stage, and the dashboard installer's `-GrantDatabaseAccess`
  rebuild could run beside the nightly. All three now take the lock and stop at once, unchanged,
  when another run holds it.
- **Reading agent ids resumes where it stopped.** `usp_BackfillAgentNames @Others = 1` started
  from the first record each time and re-read records flattened since 2.4.11 that were already
  right, so an install stopped after hours began again from zero. Now it saves its position with
  each batch, carries on from it, and skips records flattened since 2.4.11.
- **A session no longer ends before it starts.** In the hour clocks go back, a session's local
  end could read earlier than its local start. The end now never reads earlier than the start,
  and its length is still measured in UTC.
### Changed
- **Admin › Access removes a role with a small × box.** Each role in the grants list had a
  *Remove* button; it is now a small × box beside the role, with room after it, that asks before
  it removes the role.
- **The SID is checked whichever way a sign-in is matched.** An answer from Active Directory, or a
  sign-in name that is a UPN, made the account any person with no SID on record: a manager outside
  the directory table, or an administrator granted by UPN who is not in it. In another domain or
  forest an account can carry anyone's UPN. Such an answer now makes the account a person only
  when the directory records that person's SID and it is the account's. Admin › Directory quality
  › *No SID recorded* lists who this leaves out, placeholder managers included: load them with
  their SID, or map their accounts in `Insights:IdentityMap`; grant roles to accounts outside the
  directory as `DOMAIN\name` or through a group.

### Known limits
- **Microsoft 365 E7's SKU part number is assumed** (`Microsoft_365_E7`, `M365_E7`, `SPE_E7`, each with anything
  after it): no tenant with E7 was tried. Check License pool and set the license lookup's `-CopilotSuiteSku` and
  Admin's *Suites* to the name you see. The suite prices are Microsoft's list prices for July 2026 as published:
  put in your agreement's.
- **A suite move rests on Copilot and Power BI use only**: Defender, Purview, Entra ID P2, Teams Phone, Agent 365
  and the Entra Suite are not in the data, so a move off E5 or E7 is a review, never a saving counted on the page.- A person whose sign-in name changes can count as two users for a few days, until the
  directory load records the new name.
- The dashboard's labeled-content alert can count one interaction twice when it carries two
  labels that both rank high.
- Each web server keeps its own page cache, so two servers can show figures a few minutes apart;
  use sticky sessions (DASHBOARD.md, *Several web servers*).
- Page totals that once fell to 0 are fixed on the pages they did; a wider rework of how pages
  total is left for a later release.
- An administrator's change is made before its audit row is written, so if writing the row
  fails, the change stands without one.
- Admin › Directory quality › *No SID recorded* on 2.4.11 does not list managers outside the
  directory, so the check before upgrading (step 1) can miss them; on 2.4.12 it lists them.

## [2.4.11] - 2026-10-02

Agents under their own names, and page speed at three times the size. An agent now shows the
friendly name the audit log gives it, and stays one agent when its maker renames it. Measured
as the head of an organization of 210,000 people with 120 million interactions, every page
loads in under 1.5 seconds once built ahead, building ahead takes 14 minutes instead of 37,
and 200 people clicking at once are served without an error
([the figures](docs/DEPLOYMENT.md#120-million-interactions-the-2411-test)).

To upgrade: replace the scripts, run `Install-CopilotAudit.ps1 -Dashboard` once, install the
dashboard package over 2.4.10, and refresh the Power BI model once. The installer reads the
agents' names from the records already loaded, in minutes, without flattening anything again.
One new table (`audit.AgentCatalog`) and one new column (`bi.DimAgent.NamedInAudit`). No
figure changes; agents change name, never count.

### Added
- **Agents show the friendly name the audit log gives them.** A Copilot interaction record
  carries its agent's id and readable name (`AgentId`, `AgentName`) at the top of the record.
  The flatten looked for them inside `CopilotEventData`, where no record has them, so every
  agent appeared under a name cut from its identity: a Copilot Studio agent under its schema
  name, and a declarative agent (built in Agent Builder or Copilot Studio) under the last
  twelve characters of an id. Both fields are read now, from either place. A new table,
  `audit.AgentCatalog`, keeps for every agent the log names the name its latest record gave
  it, and `bi.usp_NameAgents` names `bi.DimAgent` from it at the end of every refresh. The
  dashboard, the digests, the new-agent alert and the Power BI model all show that name. A
  name an administrator gave in Admin › Reference data still shows instead of it.
- **A renamed agent stays one agent.** An agent is its id; its name is a label on the id. The
  night the first record under a new name arrives, the same agent is relabeled, in every
  period, and nothing is split or rebuilt. The catalog takes a name only from a record at
  least as recent as the one it has, so records that arrive late or out of order never undo
  a rename. `bi.DimAgent.NamedInAudit` (and `bi.Agents`, and the model) says whether a name
  is the log's own or one read from the id.
- **Records already loaded get their names without a re-flatten.** On the upgrade,
  `Install-CopilotAudit.ps1` runs `audit.usp_BackfillAgentNames` once: it reads `AgentId` and
  `AgentName` from the stored JSON of the records of agents only, writes them to
  `audit.RawLogFlattened`, fills the catalog and names the agents in `bi`. It takes minutes
  where flattening every record again takes hours, and leaves a mark so it is not done twice.
  A record whose JSON was purged keeps what it has; its agent is still named from its others.

### Changed
- **An agent is the one `AppIdentity` names.** Some records of an agent carry an `AgentId` and
  others of the same agent do not (seen on two live tenants), so keying on `AgentId` first, as
  the rule read, would have split one agent in two the day the flatten began to read it. The
  agent is the one `AppIdentity` names (`Copilot.Studio.<...>`, `MicrosoftAgent.<...>`), and
  `AgentId` only where `AppIdentity` names none. No real record was keyed the other way, so no
  agent changes key. The rule is in one place, `bi.fnAgentIdentity`.
- **The name read from an id, where the log gives none.** A declarative agent shows everything
  after `Copilot.Studio.` (`Declarative.T_<id>`), not the last twelve characters of the id. One
  of Microsoft's shows Microsoft's own name for it, the part after `MicrosoftAgent.`
  (`OutlookDraftsChatSteering`), not the internal name that follows.
- **Pages that read a list edited through the day are kept, too.** Flagged interactions, the
  three non-human account pages and the board pack read lists people change all day (triage,
  confirmations, exported license actions), so they were never kept and ran their queries on
  every visit: 0.4 to 1.2 seconds alone, and the first pages to queue under load. Each list
  now has a version of its own, read from the database for every page that shows it; a page is
  kept under it, and an edit, made on this web server or another, shows on the very next page.
  With 100 people clicking at once on whole-organization pages: 194 requests a second with a
  median of 5 ms and none failed, from 63 a second with a median of 152 ms, one in a hundred
  over 20 seconds, and two failed.
- **Retention and habit** counts in the database. The page read every person-week (2.5
  million rows at 210,000 people) and looped over them, 3 seconds on every visit, kept or
  not. It now reads how many people share each pattern of weeks: 2 seconds the first time,
  0.1 after.
- **Copilot on non-human accounts** (Licensing, Security and Compliance) works the rules out
  once a visit, not four times, compares names as plain lowercase text, and looks up the last
  day of use only for the rows it shows: 0.4 seconds, from 2.5. The alert, the board pack and
  the removal files use the same query and gain the same.
- **Security › Flagged interactions** picks its 50 rows before joining them to people, apps
  and agents when it is sorted by something the flag itself holds, as it opens: 0.8 to 1.2
  seconds for a year, from 1.5 to 1.9.
- **Admin › Reference data** reads the labels' totals in one pass and keeps them until the
  data changes: 1.5 seconds the first time and 0.02 after, from 2.7 every time.

### Fixed
- **A report could run with a query plan built for other dates.** SQL Server plans a query for
  the values of whoever runs it first and reuses the plan for everyone after. The same report
  query runs for a week and for a year, for the period and for the one before it; the period
  before a year of data is empty, and a plan built for that was then used for the year itself.
  At 120 million interactions the trend behind most pages took 77 seconds that way and timed
  out at 180 days, so ten pages failed with an error at that period and building the pages
  ahead took 37 minutes. Every read between dates now says how it wants to be planned: for a
  typical range across the whole organization, and afresh inside a manager's part of it (ten
  people or seventy thousand). The same query takes 4 to 6 seconds, the ten pages load, and
  building ahead takes 14 minutes. A plan changes how a query runs, never what it returns.
  It can happen at any size: the larger the data, the worse the wrong plan.
- **A page inside one manager's part could take two minutes and fail.** To plan a query inside
  a manager's part of the organization SQL Server has to guess how many people that is, from a
  sample of the org chart, and for some managers it guessed one. Planned for one person, the
  query read the facts again for each person really there: for a team of 584 at 120 million
  interactions, Leadership › Value and the board pack ran for two minutes and failed, every
  time, while the same pages for the team beside it took a second. The people in scope are now
  listed first, in a temporary table, and every query reads that list: counted, not guessed.
  Both pages load in 2 seconds for that team, and every page for it in 24 seconds in all.
  Found by the load test: it was the only error left at 50 and 100 people at once, and with
  it fixed no request failed at any size of crowd.
- **The CSV of Upgrade, train or revoke failed for a long list.** The people on the list were
  sent to SQL Server as one parameter each, twice, and the server takes 2,100: a page of 50
  was fine, and the download failed past about 1,050 people. They go as one list now.
- **`powerbi/generate_model.py` dropped the file types.** `FileTypes` was added to the model by
  hand in 2.4.6 and not to the generator, so running it removed the table and its
  relationship. It writes them now, with the tags they shipped with.
- **The example for naming an agent from the command line** (`-Task NameAgent`) passed the
  schema name as `-AgentId`; the task takes the agent's id, which is its `AppIdentity`.

## [2.4.10] - 2026-10-02

Clearer names, less clutter, and times in your own time zone. Nothing to rebuild: replace the
scripts, run `Install-CopilotAudit.ps1 -Dashboard` once, and install the dashboard package
over 2.4.9. No table changes, and an existing database keeps the time zone its days were cut in.

### Added
- **A time zone setting, the server's by default.** Admin › Access › Settings has a *Time
  zone*. Every time the dashboard shows is in it, and each column says which: *When (Eastern
  Time)*, not *When (UTC)*, on the audit log, pipeline runs, license reads, alerts, digests,
  extracts and training imports, in their CSV files, in the stale-data banner and in the
  pipeline and freshness alerts. Digests, alerts and extracts go out by its clock. Left on
  *Default* it is the zone the data's days are cut in; with none, the web server's own.
- **A new install cuts its days in the server's time zone.** With no `-ReportingTimeZone`,
  `Install-CopilotAudit.ps1` gave every database Eastern time, whatever the server. A database
  with no records yet now takes the installing server's own zone. A database that holds
  records keeps its zone: its days are already cut, and that cannot be changed.
- **On to the next report.** The foot of every report offers the one before it in its section
  and the one after, each with what it answers, and keeps the period and the part of the
  organization being looked at. Only reports the person can open are offered. By the end of a
  report the list of reports is a long way up, and on a phone it is folded away.
- **A wrong address gets an answer.** An address with no page behind it (mistyped, or a
  bookmark to a page since moved) returned nothing, which a browser shows as a blank page. It
  now gets *Page not found*, with the address, the reports nearest to it among those the
  person can open, and the way to the start page. The answer is still a 404; the data API and
  files are answered as before.

### Changed
- **No Period box on a report it does not change.** Reports with a window of their own
  (today's licenses, the last 30 or 90 days, all history) showed the Period box although
  choosing a period changed nothing on them. The box is left out there, and the bar says what
  the report covers ("Covers today's licenses"). The period chosen elsewhere is kept.
- **Report names that say what the report is.** *Inventory* was two reports and is now
  *License inventory* and *Agent inventory*; *Agents* (a report in the Agents section) is
  *Agents in use*; *People* (a report in Enablement, beside the People section) is *People by
  engagement*; *Signals* and *Signals by unit* are *Security signals* and *Security signals by
  unit*; *Demand* is *Demand for licenses*; *Resource access* is *Access to labeled content*;
  *Controls* is *Copilot controls*; *License cohorts* is *Retention by license month*. The
  name in the list is the title on the page (Admin › Access was titled *Access and roles*).
  Addresses are unchanged, so links and bookmarks still work.
- **Four more reports say what they cover.** Retention and habit (the last 12 complete weeks),
  Retention by license month (licenses that began in the last 12 months), Not started (today's
  licenses) and License optimization (the last 30 days and the 13-week forecast) ignored the
  period too, and now say so in place of the Period box. Every report was checked at 30 and at
  365 days for this.

- **Plain words.** *Multi-turn sessions* is *Sessions with a follow-up* everywhere (the share
  of sessions where the person replied to Copilot at least once), and *Multi-turn users* is
  *People who followed up*. "In scope" is gone: figures are "of 2,274 people in the
  organization", or "in this part of the organization" for a manager. "Aggregates only" is
  "Totals only: no one is named". Counts read as a sentence: "1 unit is combined", "3 units
  are combined", not "1 unit(s) are combined".

- **Leadership reports say Premium license where they mean it.** On Overview the chart's lines
  are *Premium license holders*, *Used Copilot, with a license* and *Free Copilot Chat only*
  (they were *Licensed*, *Active, licensed*, *Copilot Chat only*), and *Adoption by
  organization* is *Adoption in each part of the organization*. The Interactions chart says
  what an interaction is. On Scorecard each *Change* column names what changed (*Premium
  change*, *Free-only change*, *Active change*, *Interactions change*). *Worth* is *Estimated
  value* and *Against the license cost* / *Return* is *Return on license cost*, the same on
  Value and time saved and the Board pack. On Use cases, *With an agent* is *Interactions
  through an agent*.

- **Adoption reports use the same names as Leadership.** On Funnel and trend, *Licensed today*
  is *Premium licenses today* and *Active in the period* is *Adoption* (it is the same figure
  as Overview's); the funnel's bars say who each one counts (*License holders who used it in
  the period*, *Anyone using Copilot in the last 7 days*). Retention by license month no longer
  says "cohort". On Targets the second *Target* column, the value aimed for, is *Goal*. On
  Drops and spikes, *Kind* is *Drop or spike*. Under each title, the audience reads as people:
  *For Copilot program leads*, *For Copilot Studio makers and the center of excellence*.

- **Licensing reports in plain words.** *Licenses by SKU* is *Licenses by product (SKU)*, with
  what a SKU is; *Licensed but not active in the period* is *License holders who did not use
  Copilot*; *License the audit record reports* (and *License the records report* on Upgrade,
  train or revoke) is *License type Copilot recorded*, explained without field names; on Use
  ranking the *Free* and *Premium* columns say they count interactions, and its note gives
  the 2.4.8 rule for Premium; *Read (UTC)* is *Last read (UTC)*; on Departures ahead *From* is
  *Date from*. Overlapping licenses, before its data has loaded, says what is missing and when
  it fills, not which release brought it.

- **Security and Compliance say what their words mean.** Security signals explains a
  jailbreak, a prompt injection and a blocked resource in a sentence each. "In the reporting
  time zone" names the zone ("in Eastern Time") on the five reports that show an hour of the
  day. "One level down" is "each part of the organization one level below". On Oversharing
  hotspots the columns are *A person's OneDrive*, *People who reached it*, *Times read*,
  *Share labeled*, *Times blocked* (they were *OneDrive*, *People*, *Reads*, *Labeled*,
  *Blocked*); on Access to labeled content, *Days with one*, *Sites reached*, *Times blocked*.

- **Agents and Enablement.** An agent's *Source* is *Built in*, as Agent inventory calls it.
  Agents costing credits had two columns named *Without a license*: they are *Interactions
  without a license* and *People without a license*. Training effect had two named *After*:
  each column now says whose it is (*Attendees before*, *Attendees after*, *Colleagues before*, *Colleagues after*). On Champions the
  rank column has a heading, and *Back-and-forth conversations* is *Sessions with a
  follow-up*, the name the other reports use. Agent hand-offs and Messages per prompt explain
  themselves without a field name or a release number.

- **Forecasting in plain words.** The forecast explains its method and its shaded band in a
  sentence ("where the figure should fall 19 times in 20") before naming the statistics. Its
  lines are *License holders*, *Used Copilot* and *Free Copilot Chat only*, as on the other
  reports. License optimization's columns say they count licenses, and its note defines
  *Unused*, *Light users* and *Heavy demand*. *A month (USD)* is *Cost a month*, *Added cost a
  month* or *Cost change a month*, whichever it is; on License action outcomes *What* and *By*
  are *Action* and *Exported by*.
- **Governance without "grounding".** *Models and grounding* is *Models and web search*; *Web
  grounding* and *Web grounded* are *Interactions that searched the web* and *Searched the
  web*; *Agent sprawl* is *Every agent seen, by where it was built*, with what sprawl looks
  like in its note; the deferred data-loss check says what the mark is and who sets it.
- **Admin › Access explains each setting on its own line,** under the form, instead of in one
  paragraph for all of them.
- **A refused page says who to ask and where to go.** "An administrator can grant it" named
  no one. A page you may not open now lists up to three of the dashboard's administrators by
  name (people granted Admin directly; none in privacy mode) and links to the start page.
- **The start page says how current the data is, and what a role is for.** Under *Your
  access*: "The reports hold Copilot use up to 26 Sep 2026, and were last refreshed 2026-10-01
  16:49 Eastern Time", and what holding a role (or none) means for the reports listed. Its
  introduction says how to find a report, not where the data comes from.
- **Find a report finds more, from the keyboard.** A section's name or its audience brings
  all its reports ("licensing", "finance"); the box says how many matched; the down and up
  arrows walk the matches and Enter opens one; pressing / anywhere on a page puts the cursor
  in the box. With no match it suggests what to type.
- **The filter bar says what the names in it are.** The path down the organization starts
  with *Showing*, and the part being shown says how many people it covers ("Alex Saleh and 54
  people under them"); the teams in *Look inside a team* say "people" after their count.
- **One set of column names in the comparison tables.** The tables that compare departments,
  units, countries, offices, job titles, job families and tenants called the same two things
  *Licensed* and *Active*. They are *License holders* and *Used Copilot* everywhere (and
  *Share licensed* is *Share with a license*), matching the charts above them. On Use ranking
  the Yes/No column is *Has a license*. The same names head the CSV files.
- **Charts are readable on a phone.** A chart was drawn for a wide screen and shrunk to fit, so
  on a phone its text was a few pixels high. On a narrow screen every chart is now drawn for
  that width: its text is about 10 pixels, a line's end carries its value alone (the names
  are in the legend above it), a bar's name wraps onto as many as five lines, and a heatmap
  labels fewer columns. The browser tells the server the screen is narrow with a cookie that
  holds nothing else, and fetches the page once more when that changes. Six label problems
  remain at phone width, on four reports with long site addresses or their own charts.
- **A long name beside the first bar stays inside its chart.** A name of three lines or more
  (a site address, on a phone most names) rose above the top of the chart on the first row and
  lost the top of its first line. The first row now has the room.
- **Usage by license reads on a phone.** Its two charts were still drawn wide and shrunk; they
  are drawn at the narrow size like the rest.
- **On a phone the headline figures sit two to a row.** They were stacked one under another,
  so a report with five of them opened with three screens of figures before its first chart.
- **An information icon stays with its label.** Where a title or a figure's label wrapped, the
  icon could land on a line by itself. It now goes with the label's last word.
- **Admin › Health answers first.** It listed the steps and left the reader to work out
  whether the data was current. Three figures now open the page: the day the reports show data
  up to, whether the last pipeline run succeeded (and where it stopped if not), and how many
  steps are behind, by name. The steps have names that say what they are (*Report tables*,
  *Dashboard totals*, *People directory*) with a line on what each does, a way of loading
  records that a site never uses reads *Not used* instead of a warning, and the page follows
  the *Data is stale after* setting instead of a fixed 36 hours.
- **Admin forms are tidier.** A checkbox sat on a line of its own above its words; it now sits
  before them. A button's words no longer break onto two lines, a digest's actions sit side
  by side, and on Admin › Email the *Exchange Online* and *SMTP server* headings and their
  notes sit above their fields instead of beside whichever field came next. Adding a target
  offers the measures by the names the reports use (*Share of people active*, not
  *ActiveShare*), and the list of digests no longer says each name twice. On Admin ›
  Classifications a rule's pattern is called a pattern, not "Job title like".
- **Agent detail and App detail say what to do.** With nothing chosen the page said "Choose an
  agent above" over a box that was below it. The box is now headed *Choose an agent* (or *an
  app*), is drawn like the dashboard's other boxes, and links to the report where one can be
  picked by name. Messages that said "in your scope" say "among the people you can see", and
  the Leaderboard by country or company no longer counts "countrys" and "companys".
- **"Not set up yet" messages are written for the reader.** A report whose data is not there
  yet said so in installer's terms ("arrive with 2.3.0's operations schema", "not in the
  rollups yet"). Each now says what is missing in plain words, then what an administrator
  does about it.
- **Easier with a keyboard or a screen reader.** The first press of Tab on any page offers
  *Skip to the page*, past the list of reports; before, the report was a hundred links away.
  Every table's column headings are marked as such, so a screen reader reads each figure
  with its column's name.
- **A wide table shows that it continues.** A table wider than its panel scrolls sideways, and
  on a phone its last columns were simply cut off at the edge. A soft shadow now marks the
  edge a table continues past, and goes when that end is reached.
- **The error page says what to do.** *Something went wrong* now says to try again, names
  the page that failed and the time to give an administrator (which is how the error is found
  in the site's log), and links to the start page. The error itself is still never shown.
- **A heat map's cells say what their figure counts.** Resting on a cell showed "Wed, 14: 20".
  It now reads "Wed, 14:00 to 14:59: 20 interactions", and likewise on the other six heat
  maps: the share of a unit's people who used Copilot, signals per 1,000 interactions, the
  share of interactions that touched labeled content, a month's license holders week by week.
- **On a phone the report comes first.** On a narrow window the list of reports sits above the
  page and filled the first screen. It now folds behind one button that says where you are
  ("Leadership › Overview"); *Find a report* stays in view. The time zone box no longer runs
  off a narrow screen.
- **Long column headings wrap instead of making the table scroll sideways.** A heading now
  breaks onto a second line between words, so wide tables (Scorecard, Use ranking, Agents
  costing credits, Training effect) show more of their columns at once; a figure with its
  arrow or unit stays on one line.

### Fixed
- **An old link to someone's organization showed zeros under "Whole organization".** A link
  with a manager who is no longer in the directory (`?org=` with a sign-in nobody has) opened
  a report that said *Whole organization* and showed nothing. A page now shows the whole
  organization and says why. Someone who sees only their own part, following a link to a part
  that is not theirs, got their own part with no word of it; the page now says so, in the
  same words whether or not such a person exists. The data API and the digests are unchanged:
  they keep to what was asked for.
- **The audit log counted data API calls as admin changes.** Admin › Audit log showed two
  figures named *Admin changes*; the second was the data API's calls. Each kind now has its
  own name (*Page views*, *Refused*, *Exports*, *Data API calls*, *Admin changes*), used by
  the figures, the filter and the Kind column alike; the download keeps the words the log
  stores (View, Denied, Export, Api, Admin). "by 1 people" is "by 1 person".
- **Pointers to a page that does not exist.** Five places sent the reader to *Admin >
  Settings* for the pay-as-you-go price, and the license export script to *Admin > License
  pool*. The price is set in Admin > Access, and the pool is Licensing > License pool.
- **Date labels ran into each other on the Forecast charts** with a 52-week forecast (a year of
  history and a year ahead in one chart). A chart now draws fewer date labels when they would
  not fit beside each other.
- **The license cost of a period was two amounts.** Value and time saved took a month for 30.4
  days and the Board pack for 30, so 1,269 licenses over 30 days cost 37,569 on one and 38,070
  on the other. Both now take a month for a twelfth of a year (37,548 for that example; a full
  year is exactly twelve months' cost).
- **"For copilot program lead".** The line under a report's title lowercased the first word
  of its audience whatever it was: *For copilot program lead*, *For hR and people analytics*,
  *For aI governance and architecture*. Names and abbreviations keep their capitals.
- **License optimization drew departments in a different order from one visit to the next**
  when two had the same gap between unused licenses and demand. Ties are now in name order.

## [2.4.9] - 2026-10-02

Adoption no longer dips at weekends, an administrator can turn any report off, and a rebuild
that was stopped part way finishes itself.

Install over 2.4.8: replace the scripts, run `Install-CopilotAudit.ps1 -Dashboard` once, and
reinstall the dashboard from its package. If the reporting tables hold fewer interactions than
were flattened, the installer rebuilds them in full, with every day of the rollups.

### Added
- **Admin › Reports shown: turn any report off.** Every report is listed by section, laid out
  as the start page is, each with a box to tick; a section's own box ticks or clears all of it.
  A report that is off is gone for everyone, administrators too: from the navigation, from the
  start page, and at its address (404), with its exports and forms. Ticking it brings it back;
  nothing is deleted. Roles and levels still apply to the reports that are on, and Admin pages
  cannot be turned off. Digests, extracts, the board pack and the data API are not changed by
  it. Kept in `dash.Setting` (one row a report), so there is no schema change.

- **The navigation is short, and says what each report is.** The list on the left was every
  report at once, three screens long. Sections now fold, with the one you are in open:
  selecting a section opens its reports under it or folds them, and moves nothing else in the
  list. The list stays on screen while a report scrolls. *About these reports*, under an
  open section, opens a panel of its reports under headings (Licensing: inventory and pool,
  cost savings, policy breaches, changes ahead), each with a sentence on what it answers.
  *Find a report* narrows the list as you type, by name or by what a report answers. Resting
  on a link, or reaching it by keyboard, shows that sentence beside the list.
- **Your most used.** The first section of the list, and the first card of the start page:
  up to ten reports the person opened most in the last 90 days, once they have opened three.
  Counted from the page views already in the audit log, once a day; nothing is saved for it
  and no table is added. A report they can no longer open, or one turned off, is left out. The start page is the full index, with the same headings and sentences. Licensing,
  Adoption and Leadership list their reports in the order of their headings.

### Fixed
- **The adoption rate on Leadership › Overview fell every weekend.** It showed the share of
  license holders active on each day, which is use, not adoption. It is now, at each point, the
  share of the people holding a Premium license who used Copilot in the 28 days up to it: a
  rolling four weeks, so weekends and holidays do not move it. How many used it each day stays
  in *People using Copilot*, beside it. The first 28 days of the data read low.
- **A full rebuild of the reporting tables that was stopped part way left months missing, and
  nothing noticed.** The rebuild empties `bi.FactInteraction` and fills it month by month; an
  interrupted run left the later months out, the installer saw nothing to do, and the nightly
  refresh added new days after the gap. `bi.usp_RefreshBi` now leaves a mark
  (`bi.RebuildInProgress`) while a full rebuild runs, and any refresh that finds it rebuilds in
  full. The installer also rebuilds, with every day of the rollups, when the mark is there or
  when the fact holds fewer than 99 in 100 of the flattened records (a rebuild cut short before
  2.4.9).

### Changed
- **Leadership › Overview names the license.** The adoption chart is titled *Adoption rate:
  Premium license holders* and its note starts with it; *Licensed today* is *Premium licenses
  today*.

## [2.4.8] - 2026-10-01

Licensed use is read from what Microsoft writes into each record instead of being inferred, and
"licensed since" is filled.

Install over 2.4.7: replace the scripts, run `Install-CopilotAudit.ps1 -Dashboard` once (it
rebuilds the reporting tables and every day of the rollups, once, which takes as long as a full
refresh), run `tools\Update-CopilotAuditEnrichment.ps1 -AllLicensedUsers` once to fill the dates,
and reinstall the dashboard from its package.

### Fixed
- **A license used for Copilot Chat in work mode could be listed for revocation as "used only web
  Copilot Chat".** Premium use in chat was inferred: the record listed a resource and did not
  search the web. That was wrong three ways. A chat that read work files and also searched the
  web counted as free. A work-mode chat that read nothing counted as free. And the web pages a web
  search cites are themselves listed as resources, so the test did not separate web from work.
  The rule now reads the record's app identity: `Copilot.M365Copilot.Bizchat` (work mode) and
  `Copilot.M365Copilot.Apps` (inside an app) are licensed use, `Copilot.M365Copilot.WebChat` is
  not unless it read work content, and `Copilot.Studio.*` agents are not. Work content is a
  resource in SharePoint or OneDrive, Outlook or Teams, or a mail, a Teams message, a meeting or a
  connector item. A record without one of these identities is decided by its app's category and
  work content, as before. `bi.FactInteraction.UseKind` keeps the answer for each interaction.
  Web Copilot Chat opened inside Teams or Outlook, which counted as Premium, no longer does.
- **Use before the "licensed since" date counted as unlicensed.** Entra's date is when the
  current assignment began, which a reassignment moves forward. A record Copilot itself marked
  Premium now counts as licensed at the time, whatever the dates say.
- **"Licensed since" was empty.** The license tool looked for the word Copilot in the name of the
  service behind each of a person's plans, which most Copilot plans' services do not contain. It
  now matches the Copilot SKU's own service plans by id (those no other SKU in the tenant
  carries) and keeps the earliest live assignment. With the date filled, the revoke list's
  "licensed for at least 30 days" rule applies again.

### Changed
- **Upgrade, train or revoke says what it found.** A revocation candidate who used Copilot reads
  "No licensed use in the records: N days in 30 of web Copilot Chat or agents only", and the rule
  above the list names what counts.

## [2.4.7] - 2026-10-01

Every page loads in under 2 seconds for a year, measured as the head of an organization of
70,000 people with 40 million interactions; and Forecasting › License optimization, which
2.4.6 broke, builds again.

Install over 2.4.6: replace the scripts, run `Install-CopilotAudit.ps1 -Dashboard` once (it
adds `dash.FlaggedInteraction`, which the next refresh fills), and reinstall the dashboard
from its package. The site builds its reports in the background a few seconds after it
starts; Admin › Health › *Report speed* shows when it has finished.

### Fixed
- **Forecasting › License optimization failed for everyone in 2.4.6** (*Cannot perform an
  aggregate function on an expression containing an aggregate or a subquery*): the protected
  levels are now joined instead of tested inside its counts. The layout audit that should
  have caught it now fails on a page that shows the error page.

### Changed
- **Report pages keep their query results until the data changes, and are built ahead.** The
  data changes at the nightly refresh and when an administrator changes a setting or
  reference data; in between, the same query gives the same rows. Results are kept in memory
  under the tenant, the query's text and every parameter (the scope among them), and
  dropped when the dashboard is refreshed, a pipeline run is recorded, the licenses are read,
  or a setting or reference table changes, here, on another web server or from
  `Invoke-CopilotMaintenance.ps1`, and at midnight UTC. After each change the site builds
  every report for the whole organization at every period in the background (426 page
  views in under 5 minutes at 40 million interactions), so the top two levels and
  Organization holders get every page from memory; a manager's own part of the
  organization is kept from its first view. Who the viewer is and what they may see are
  settled from the database on every request, as before. Admin pages, exports, the data
  API, forms, and lists edited all day (flag triage, exported license actions, confirmed
  accounts) always read the database. `Insights:QueryCacheMinutes` (720; 0 turns it off) and
  `Insights:WarmPages` are in the site's settings; Admin › Health has a *Report speed* panel.
- **Security › Flagged interactions reads a list of the flagged interactions the refresh now
  keeps** (`dash.FlaggedInteraction`), instead of reading their record ids out of the whole
  interaction fact: 4.4 seconds to under one for a year at 40 million. This page is never
  answered from memory, since its triage changes all day.
- **Forecasting's weekly history is read in one pass** instead of once for each of its five
  columns: 4.9 seconds to about one, for Forecast and License optimization.

## [2.4.6] - 2026-10-01

Install over 2.4.5: replace the scripts, run `Install-CopilotAudit.ps1 -Dashboard` once, and
reinstall the dashboard from its package. The installer adds the file type of every
resource Copilot read, and, because the resources already loaded have none, **refreshes
bi in full once**: as long as a full bi refresh takes (2 to 4 hours at 20 million
interactions), with the data readable throughout. It also adds the `ProtectedLevels`
setting (5 until changed). Then refresh the Power BI model once: it gains a FileTypes
table.

### Added
- **Adoption › Usage by license** (Adoption role), one page for licensed, unlicensed or
  everyone in the part of the organization chosen in the filter bar: people, active and
  inactive, as counts and shares; Copilot use by app, and by the file type it read;
  the highest and lowest users; minutes used, by person and by app, with average cards for
  Outlook, Teams and Copilot Chat, and every conversation longer than one prompt in a
  sortable, searchable list with its CSV; interactions by app over the period, with each
  app's lowest, highest and mean point; and people by days active. It shows when the
  database was last updated and the latest audit record it holds. Minutes are the span of
  each conversation from its first prompt to its last, so they are a floor on the time
  spent. Measured at 40 million interactions over a year, each of its queries takes under
  3 seconds and they run side by side.
- **The file type of every resource Copilot read**: docx, xlsx, pptx, csv and so on, taken
  from the resource's name, and for anything that is not a file its kind (a mail, a Teams
  message, a Stream video, a whiteboard, a web search). It is `bi.FactAccessedResource.
  FileTypeKey`, named in the new `bi.DimFileType`, in Power BI's AccessedResources with a
  new FileTypes table, and on Usage by license as *Copilot use by source*.
- **Leaders are never on a revoke or train list.** Levels 1 to 5 of the organization (Admin ›
  Access › *Never on revoke or train lists*, any number from 1 to 15, or none; also
  `Invoke-CopilotMaintenance.ps1 -Task SetDashboardSetting -Name ProtectedLevels`) are left
  out of Upgrade, train or revoke (train and revoke), the Swap list's take side, Unused
  licenses, Better on pay-as-you-go, Fading use, Not started, Enablement's one-off and
  not-started people and License optimization's counts, and so out of their CSVs, the Entra
  bulk files and scripts, the Licensing digest's counts, the Board pack, Budget and renewal,
  and the data API. Each list says which levels it leaves out. Lists of accounts nobody can
  use keep everyone: Licenses on disabled accounts, Departures ahead and Copilot on
  non-human accounts.

### Fixed
- **The data API's `/api/v1/license-actions?kind=train` and `kind=revoke` failed** with
  *Invalid column name 'P'*: they now read the same activity as the page, so they list the
  same people.

## [2.4.5] - 2026-10-01

A Copilot license on an account that is not a person is now reported as a policy breach,
with an alert, removal files and administrator-confirmed exceptions. The Scorecard splits
adoption into Premium and free-only use, every table sorts by its column headers, trend
charts keep event names off the data, adoption rankings no longer rest on one or two
people, and every upload has a mapping file to download.

Install over 2.4.4 (or 2.4.3): replace the scripts, run `Install-CopilotAudit.ps1
-Dashboard` once (it adds `dash.NonHumanException`, the non-human rules' settings, and
the dashboard's read access to `audit.GroupMember`), and reinstall the dashboard from its
package. Then check the rules in Admin › Reference data › *Non-human accounts*: the
built-in ones are on, and your service-account employee types and AD groups are yours to
add. The first check after the install alerts once for every breach it finds.

### Added
- **A Copilot license on a non-human account is a policy breach.** The page that listed
  them for Licensing (*Licenses on non-person accounts*, 2.4.3) is now *Copilot on
  non-human accounts*, in Security and Compliance as well as Licensing, for each role. It
  counts the breaches in red, says why each account was flagged, and has an Entra bulk
  remove file and a PowerShell script to remove every one, recorded for License action
  outcomes. The board pack's risk table counts them.
  - **The rules are yours** (Admin › Reference data › *Non-human accounts*): guests;
    sign-ins and names that read like a service, shared, test or room account; accounts
    with no manager outside the org chart; and, new, sign-in patterns of your own, the
    employee types that are not people, and AD groups of service accounts, whose members
    the nightly directory load now reads.
  - **An alert** goes to the Security and Licensing recipients, the webhook and the event
    log (ID 2002) the first time each account is found, and again if it comes back after
    it was cleared. Account names are left out in privacy mode, where the page is hidden.
  - **An account that is a person after all** is confirmed by an administrator on the
    page, with the reason (`dash.NonHumanException`): it stops being a breach and raises
    no alert, and *Flag it again* undoes it. Both are audited.
- **Leadership › Scorecard splits adoption into Premium and free only**: license holders
  who used what the license pays for (Copilot in Word, Excel, PowerPoint, Outlook or
  Teams, or chat over work content), and those who used only what is free without it.
  The two add up to Adoption; each has its change on the period before, as KPIs and for
  each unit. Units high on free-only adoption are candidates for Licensing › Better on
  pay-as-you-go. Until the rollups are rebuilt with 2.3.0, the page says so and keeps the
  single figure.
- **Every report's tables sort by their column headers.** Click a header to sort by it,
  and again to reverse. Numbers, percentages, money and dates sort by their value, not
  their text; numbers and dates open largest or latest first, text A to Z. A long list of
  people, which comes 50 rows at a time, is sorted in the database, so the order covers
  every page of it and its CSV export, not only the rows on screen. The sort choice beside
  its search box says when a column or a reversed order is in force. Columns of buttons
  don't sort, nor do tables whose order is their meaning, such as a funnel's steps or
  weeks in sequence, and a *Smaller groups* or total row stays at the bottom. The arrow
  beside each header shows the sort, and screen readers announce it.

- **Download the mapping file** beside every upload. Admin › Value names: every value of
  every column with its name and note in one CSV (or one column's from its view), in
  exactly the columns the upload reads. Admin › Training records: every attendance record
  held, as UPN, session and date. Download, edit, and upload it back; values a download
  guarded against spreadsheet formulas (a leading `'`) are read back as they were.

### Changed
- **Adoption rankings rest only on groups with at least the minimum group size of license
  holders** (5 by default): Adoption › Departments, Leadership › Organization and Board
  pack, Regions (countries and offices) and People (job families, employee types,
  managers and individual contributors). A department whose 2 license holders both used
  Copilot showed as 100% and, with many like it, filled the top of the chart. Those groups
  are now one combined bar at the end, ties are broken by the number of license holders,
  and every bar shows its "36 of 40" beside the rate, not only on hover. The Departments
  table lists such departments after the ranked ones.
- **Events on trend charts are numbered badges above the chart, with a key below it.** Their
  names were written inside the plot, over the lines, where they collided with each other
  and, on half-width charts, piled up over the data. The dashed line stays, lighter; events
  in the same week share a badge; hovering a badge shows each event and its kind.
- **Licensing › Overlapping licenses leaves out licenses that cost nothing a user**: the
  Copilot Studio user license (`VIRTUAL_AGENT_USL`, and its GCC and GCC High forms), viral
  sign-ups and trials. Removing one saves nothing, so they are no longer listed as
  overlaps.

### Fixed
- **Information icons' text is no longer covered by the pointer.** It now opens under the
  icon instead of following the pointer, and the icon uses the hand pointer rather than
  the help pointer, whose large question mark sat on top of the text.

## [2.4.4] - 2026-10-01

The dashboard can be limited to the top levels of the organization, set in the dashboard
itself, and the personal features are gone: My Copilot, saved views and the personal
weekly email. No table changes.

Install over 2.4.3: replace the scripts, run `Install-CopilotAudit.ps1 -Dashboard` once
(it adds the `MaxViewerLevel` setting, off until chosen, and removes the personal email's
setting), and reinstall the dashboard from its package. Then choose the levels in
Admin › Access › *Who can open the dashboard*.

### Added
- **Who can open the dashboard** (Admin › Access): *Everyone in the directory*, as
  before, or *Levels 1 to N* below the top of the organization, up to 15, each choice
  showing how many people it lets in. With a limit, everyone else is refused every page,
  CSV export, file download, form and data API call, with a page that names the levels,
  and each refusal is in the audit log; the navigation lists nothing for them. Anyone
  granted a role other than Admin is let in whatever their level, so Admin › Access is
  the list of exceptions. An administrator outside the levels keeps the start page and
  the Admin pages, so a wrong limit, or a wrong top, can always be put right. Manager
  digests go only to managers within the levels. The level comes from the directory
  record of the SID-checked sign-in, never from the request. Also
  `Invoke-CopilotMaintenance.ps1 -Task SetDashboardSetting -Name MaxViewerLevel -Value N`
  (0 for everyone). *Check someone's access* says when the limit keeps a person out.
- **Managers who do not get the digest** (Admin › Alerts and digests): an administrator
  stops, or restores, one manager's digest. Opt-outs made before 2.4.4 are kept.

### Removed
- **My Copilot**: *My use*, *Email* and *Saved views*, with the *Save view* button on
  every report and the saved start page. Links to `/me` in earlier emails open the start
  page. `dash.SavedView` and `dash.DigestPreference` are kept as they are, with their rows.
- **The personal weekly email**, and its switch in Admin › Alerts and digests.

### Fixed
- **Admin › Health moves to `/admin/health`.** It was the only page at the bare
  `/admin`, an address web servers commonly block with a server-wide rule (URL Rewrite
  or Request Filtering matching `^.*/admin/?$`). There it answered IIS's *404.0 - Not
  Found* from *IIS Web Core* while every other Admin page, all below `/admin/…`, worked.
  The menu and the links in the pipeline, stale-data and certificate alerts point at
  the new address, and `/admin` redirects to it where nothing blocks it, so bookmarks
  and earlier alerts still arrive.

## [2.4.3] - 2026-09-30

Eight new pages that each end in an action that saves money, every report saying who
it is for and what to do with it, and a license tool that survives a dropped
connection. Measured on 70,000 people and 40 million interactions as the head of the
organization, each new page loads in under 2.5 seconds for a year.

Install over 2.4.2: replace the scripts, run `Install-CopilotAudit.ps1 -Dashboard` once
(it adds `audit.UserLicenseSku`, two leave-date columns, and the dashboard's read access
to the new table), and reinstall the dashboard from its package. No data is reloaded.
The next license tool run fills the SKUs, and the next directory load AD's account
expiry. Entra's leave date is optional: it needs the Graph permission
`User-LifeCycleInfo.Read.All` and `IncludeLeaveDates = $true` in the settings file.

### Changed
- **Every report says who it is for, what it helps with and what to do, with less text on
  the page.** Two short lines under each title, *Helps you* and *Do*, with the section's
  audience; the introduction, each panel's explanation and each KPI's wording move behind
  information icons that open on hover, keyboard focus or a tap, close with Escape, and
  print in place. Short KPI notes with a figure stay on the card; Admin pages keep their
  instructions visible. The lines for all 70 reports are in one place (`Pages/Guidance.cs`),
  and a test fails if a report in the navigation has none.

### Added
- **Licensing › Better on pay-as-you-go** (Licensing role): license holders who use
  Copilot but little of what only the license gives, Copilot in Word, Excel,
  PowerPoint, Outlook and Teams, or chat over work content. Choose how little: no
  Premium use, or at most 5, 10 (the default) or 25% of their use. Each person shows
  their use, their Premium and agent interactions, what Copilot Credits would cost
  them a month and the saving against the license; the page totals the saving. The
  credit price is a new setting, **pay-as-you-go cost an interaction** in Admin ›
  Settings (`PaygInteractionCost`, 0.12 by default: 12 Copilot Credits at 0.01), also
  settable with `Invoke-CopilotMaintenance.ps1 -Task SetDashboardSetting`.
- **Licensing › Better with a license** (Licensing role): the reverse of Better on
  pay-as-you-go. People with no Copilot license whose use is charged in Copilot
  Credits, through agents or Copilot Chat over work content, at a monthly rate at or
  above the license cost, with the credits, the difference, the total saving and a
  file to license them (recorded for License action outcomes). *Everyone paying in
  credits* lists them all.
- **Licensing › Fading use** (Licensing role): license holders of at least 90 days
  who used Copilot on at least 4 days a month over the 60 days before the last 30,
  and whose days of use in the last 30 fell by half (or three quarters) but not to
  none: coach them, through the manager each row names, or plan to reclaim before the
  license goes idle. The page shows what their licenses cost a month.
- **Licensing › Licenses on non-person accounts** (Licensing role): Copilot licenses on
  enabled accounts that may not be a person, or not one of the organization's: guests
  (an `#EXT#` sign-in or Entra's user type), sign-ins or display names that read like a
  service, shared, test or room account, and accounts with no manager outside the org
  chart. A second table counts license holders by employee type, each type opening
  its list, for contractors and the like. Disabled and deleted accounts stay on
  Licenses on disabled accounts.
- **Agents › Agents costing credits** (Agents role): each agent's use by people
  without a Copilot license at the time, what it costs in Copilot Credits a month, and
  what that comes to a person, with a suggestion: restrict or retire an agent few use
  that costs a license's worth or more, or license the users of one whose people each
  cost more than a license (Better with a license).
- **Licensing › Overlapping licenses** (Licensing role): people holding the Copilot
  license together with another Copilot-family license: a second Copilot SKU, Copilot
  Studio or its trial, Copilot for Sales or Service and the like. By SKU, with the
  tenant's seats of it, and by person, with when they last used Copilot. It reads a
  new table, `audit.UserLicenseSku`: the license tool now keeps every SKU each person
  holds, not only Copilot's (the same Graph call, no new permission), and replaces it
  on each run.
- **Licensing › Departures ahead** (Licensing role): license holders due to leave in
  the next 7, 30 or 90 days, and those past their date whose account is still enabled,
  with a removal file for the past ones (recorded as leavers for License action
  outcomes). The leave date is the earlier of two new sources: AD's `accountExpires`,
  which the directory load now reads (`audit.UserDirectory.AccountExpiresUtc`), and
  Entra's `employeeLeaveDateTime`, which the license tool reads with the new
  `-IncludeLeaveDates` (`audit.CopilotLicense.EmployeeLeaveUtc`; it needs the optional
  Graph permission `User-LifeCycleInfo.Read.All`; set `IncludeLeaveDates = $true` in the
  settings file for the nightly run).
- **Licensing › Swap list** (Licensing role): each idle license (the revoke rule of
  Upgrade, train or revoke) paired with someone who wants one (its upgrade rule) in
  the same department, the same division, or anywhere in scope; the least-used
  license goes to the keenest user. It counts the swaps, the seats not bought, and
  the people left over on each side, and offers the take-back and give-out files
  (Entra bulk members file or PowerShell script) for the matched pairs only, recorded
  for License action outcomes.

### Fixed
- **The license tool no longer stops on a dropped connection to Graph.** A run over
  64,864 users failed after twenty minutes with *An existing connection was forcibly
  closed by the remote host*, and, since nothing is written until every user has been
  looked up, lost everything it had read. A failed `$batch` request is now retried,
  up to seven times with a growing wait, as a throttled one already was; the log
  shows each retry. The integration test's Graph mock drops its first batch request
  to prove it.

## [2.4.2] - 2026-09-30

Page speed for the top of a large organization, sections shown only to the roles
they belong to, several roles in one grant, and the fixes found in a first
production install of 2.4.0 (2.4.1, not released on its own). Install over 2.4.0:
replace the scripts and the dashboard and run `Install-CopilotAudit.ps1 -Dashboard`
once. It widens one `bi` column in place and adds two indexes to the dashboard
rollups, a minute or two at 70,000 people. No data is reloaded.

### Changed
- **Every page loads in under 7 seconds for a whole organization over a year.** Measured on
  a test database of 70,000 people, 40 million interactions and 70 million resources
  read, as the head of the organization at 180 and 365 days, 20 pages took over 8
  seconds and the slowest never loaded (Governance › Models and grounding, stopped
  by the two-minute query limit). After the fixes the slowest takes 6.6 seconds and 51
  of the 61 take under 3; together they went from 9 minutes to under 2. Each fix was
  checked to return the same figures:
  - **Columnstore indexes on `dash.UserDay` and `dash.ResourceDay`.** Most pages read
    these rollups for the trend and the totals; a year of them now takes a fraction
    of the time. The refresh loads them as before.
  - **The totals behind most pages' headline figures** are aggregated once, not once
    for each figure: 4 seconds to half a second.
  - **Names from Admin › Reference data are joined, not looked up by
    `dash.fnValueName`** in the queries over the facts. The function kept each of those
    queries to one processor, and where it sat in a `GROUP BY` it ran once a row: the
    Governance page's model query took over three minutes.
  - **Use cases** read the classification rules once and count people per use case
    without a distinct count: 22 seconds to under 2. Its totals come from the same
    pass rather than a second one over the facts.
  - **Oversharing hotspots** counts the people per site on its own and the rest per
    site and label: 11 seconds to 3.
  - **Resources read, by type and label**, **What Copilot read, by type**, **Models and
    grounding** and **Apps** group on keys first and name the groups afterwards.
  - **Flagged interactions** reads the flagged records once for the list and the open
    count, rather than twice.
  - **New joiners** finds each person's first use in one pass rather than one lookup a
    person.
- **The license tool asks Graph about twenty users at a time** (`$batch`), retrying any
  Graph throttles, and logs its progress every 1,000 users. One request a user took
  hours for a large tenant, with nothing in the log until the end.

### Added
- **A section with a role is hidden altogether without it.** Adoption (the Adoption
  role), Licensing and Forecasting (Licensing), Security, Compliance, Agents and
  Enablement are left out of the navigation and the start page for anyone without the
  role, their aggregate pages included, and the pages and their API calls are
  refused. This holds for whole-organization viewers too: grant executives the roles
  whose sections they should see. My Copilot, Leadership, Regions, People, App owners
  and Governance stay open to everyone for their own part of the organization.
- **Admin › Access grants several roles at once.** Tick every role the person or group
  should have; the grants list shows one row per person or group with all their
  roles, and a *Remove* beside each.

### Fixed
- **The `bi` refresh no longer fails on a long resource action**
  (*String or binary data would be truncated in table #rsrc, column 'ResourceAction'*),
  found on a 19-million-record CSV import whose longest action was 436 characters.
  Actions are flattened at up to 450; `bi.DimResourceAction` and the refresh's work
  table took 200. Both now take 450, the installer widens an existing
  `bi.DimResourceAction` in place, and the refresh cuts each resource type, action and
  site to the width it is stored in. A test checks every `bi` resource dimension
  against the column it copies.
- **The home page's browser tab** read "Copilot Insights · Copilot Insights". It now
  names the product once; every other page still reads "<page> · Copilot Insights".
- **People with no UPN are left out of the org chart, not only the directory.** They
  were already not loaded (no UPN, no Copilot use), but a manager with no UPN still
  broke the chain: the people under them were loaded with no manager, became tops of
  their own, and dropped out of the scope of everyone above. The directory load now
  gives each person the nearest manager above who has a UPN, and leaves people with
  no UPN out of the `Mgr1`…`MgrN` chain. Takes effect at the next directory load.
- **My Copilot › My use no longer offers "Whole organization" and "Look inside a team".**
  The page is only ever about the viewer, so its filter bar shows the period alone.
- **The user directory load no longer fails after loading.**
  `tools/Export-UsersWithHierarchy-ToSql.ps1` wrapped the list of AD groups granted
  Organization in a second array, so with none of them (always so without the
  dashboard), or with two or more, it stopped with *Cannot process argument
  transformation on parameter 'Group'*. The users were loaded, but the group step
  failed, and with it the nightly run.
- **An account already in a database under another name is used, not added again.**
  `Install-CopilotAudit.ps1 -ServiceAccount` and the dashboard's database grants stopped
  with *The login already has an account under a different user name* when the
  account owned the database (it is `dbo` there, as when setup runs as the service
  account itself) or a DBA had added it by another name. They now find the user by the
  login's SID: an owner needs nothing more, and another name gets the rights under
  that name.
- **Dashboard installer, run again:**
  - a host-name HTTPS binding the first run made without SNI (`New-Website -Ssl` does)
    is switched to SNI, so it can share port 443 with another site's certificate;
  - without `-CertificateThumbprint`, an HTTP binding is added when the site has none,
    so a site first made with HTTPS doesn't end up answering on nothing;
  - `Grant-CopilotInsightsAccess.sql` separates each database's grants with `GO`, so a
    DBA can run the file for several tenants in one go.
- **Dashboard installer: the SQL connection is tried before the site is.** A SQL Server
  certificate the web server doesn't trust now shows as a warning naming
  `-TrustServerCertificate`, rather than as *Something went wrong* on every page.

### Documentation
- DEPLOYMENT.md: which PowerShell runs each script; the SQL Server connection errors
  and what each means; a service account with a password instead of a gMSA; running
  setup as the service account; checking `employeeType` codes before the first load;
  starting from Purview exports before the app registration is approved; what the
  web server needs before the dashboard's installer. Release links point at 2.4.2.
- DASHBOARD.md: every installer parameter, including `-AdminPrincipal` (people) against
  `-AppPoolIdentity` (the site's account); the Hosting Bundle against the SDK; host
  name, DNS and certificate, and starting on the server's own name or HTTP; another
  site on port 443; NTLM only.
- RUNBOOK.md: troubleshooting rows for each of the above.

## [2.4.0] - 2026-09-27

A consistency release. Each figure is now computed in one place, the product
uses one spelling, retries follow one policy, and each page says which dates it
covers. Power BI now honors Organization grants made to AD groups. Nothing is
added for the reader of a report, but several names change, so **2.4.0 is a
fresh install**. There is no upgrade path from 2.3.0: drop the databases (or
point the installer at new ones), install, and run the history load again.

### Setting up
The same commands as 2.3.0; any parameter with License in its name is now spelled
the American way (see the renames below):
parameter used it:

```powershell
.\tools\Install-CopilotJobServer.ps1 -SqlServer SQLPROD01 -ServiceAccount 'CORP\gmsa-copilot$' `
    -TargetDomain users.contoso.com -TenantId contoso.onmicrosoft.com -RegisterApp -Dashboard
.\Invoke-CopilotPipeline.ps1 -History
# web server
.\dashboard\Install-CopilotInsights.ps1 -PackagePath .\copilot-insights-v2.4.0.zip -SqlServer SQLPROD01 `
    -AdminPrincipal 'USERS\Copilot-Insights-Admins' -GrantDatabaseAccess
```

### Changed: one spelling, License (breaking)

- **Tables and views:**
  - `bi.DimLicenceStatus` → `bi.DimLicenseStatus`
  - `bi.FactLicenceChange` → `bi.FactLicenseChange`
  - `bi.LicenceChanges` → `bi.LicenseChanges`
  - `bi.LicenceStatuses` → `bi.LicenseStatuses`
  - `dash.LicenceAction` → `dash.LicenseAction`
  - `dash.LicenceContract` → `dash.LicenseContract`
- **Columns, dashboard settings and Power BI names:**
  - columns such as `LicenseStatusKey`, `CopilotLicenseToday` and `LicenseChangeCount`;
  - the dashboard settings `LicenseCurrency` and `LicenseMonthlyCost`;
  - the Power BI table `LicenseChanges`.
- **Script parameters:**
  - `-LicenseDatabase` (Install-CopilotAudit, Install-CopilotJobServer, Add-CopilotTenant);
  - `-LicenseEventDatabases` (Install-CopilotAudit, Invoke-CopilotMaintenance);
  - `-SkipLicenseDatabase`;
  - `-SessionLicenseDatabase` (the backfill and the CSV import);
  - `-LicenseEventDatabase` (Export-CopilotSessionCsv);
  - the matching key in `copilot-audit.settings.psd1`.
- **Text:** every page, guide and message now says license.

### Changed: American spelling throughout

- **Every other British spelling,** after License: organization, honor,
  recognize, catalog, behavior, labeled, pseudonymize and the rest, in names
  and text alike.
- **Names that change:**
  - the Power BI role *Organisation* → *Organization*;
  - `dash.UserDay.LabelledContent` → `LabeledContent`;
  - the privacy mode value `Pseudonymise` → `Pseudonymize`.
- **Not changed:** values that are Microsoft's own, such as Graph's
  `cancelled` status and every field name in the audit records.

### Changed: computed once

- **Premium use and the use case are stored on each interaction.** Premium use is
  whether an interaction needed the Copilot license. They are two new columns,
  `bi.FactInteraction.PremiumUse` and `UseCaseKey`, written by the bi refresh.
  - The dashboard's rollups, the use-case reports, `bi.Interactions` and the
    Power BI model all read these columns. Before, each one applied the rules
    itself, so a rule change could leave one figure disagreeing with another.
  - An integration test checks that the stored value, the view and the rollups
    agree with the rule.
- **One retry policy.**
  - A digest or scheduled extract that fails is tried again an hour later,
    three tries in all, and then waits for its next period. Before, digests
    were marked sent after a failure and extracts were not retried. Manager
    and personal digests already worked this way.
  - Webhooks retry on the spot, after 5 and then 20 seconds, but not when the
    address or payload is wrong (a 4xx other than 408 or 429).
  - The status line shows each try.
- **Extracts are dated in the reporting time zone,** like every other figure.
- **Each page says which dates it covers.** Pages that don't follow the period
  filter now show *This page: …* in the filter bar, where the period normally
  appears. These are Actions, the forecasts, unused licenses, leavers and the
  rollout-wave cohorts. Before, a sentence in the notes said so, in different
  words on each page.
- **Chart text is the same size in every panel.** A chart in a half- or
  third-width panel is drawn at that panel's width instead of being scaled
  down, and no chart scales up past 1.3 times. Before, text in the smaller
  panels came out about half the size.

### Added

- **Power BI honors Organization granted to an AD group.**
  - The nightly directory load expands every group granted Organization into
    its members, nested groups included, and writes them to the new table
    `audit.GroupMember`, rewritten every night.
  - `bi.OrgSecurity` gives those members the whole organization, as the
    dashboard already did at sign-in. Before, only people granted Organization
    by name, and the top two levels, saw everyone in Power BI.
- **Admin › Alerts and digests can run the scheduled checks for one tenant or all of them.**
- **The dashboard guide states what the product writes.**
  - `docs/DASHBOARD.md` has a section on what the product reads from
    Microsoft 365 (read-only) and a table of everything it writes.
  - It writes only to its own database, except for license changes, which
    apply only after someone approves them.
- **`tools/dev`.** The UI layout audit and the generator for
  `docs/SCHEMA.md` are now in the repository, with a release checklist
  (`tools/dev/README.md`).

### Fixed

- **A change of app category reaches every day.** Changing an app's category
  (`Invoke-CopilotMaintenance -Task SetAppCategory`, without `-Apply`) used to
  change only the days loaded afterwards. Now the next bi refresh corrects
  Premium use on every earlier interaction of that app, and the next dashboard
  refresh rolls up again from that app's first day, Copilot Chat counts included.
- **Admin › Alerts and digests, running the checks for every tenant,** no longer leaves the request on the
  last tenant it ran.
- **PSScriptAnalyzer:**
  - Functions that call ShouldProcess now declare it, so `-WhatIf` reaches them
    (Install-CopilotJobServer).
  - Empty `catch` blocks now log why they are empty.
  - Plural function names are renamed.
  - A parameter named `$sender` (an automatic variable) is renamed.
- **Encoding:** two test files with non-ASCII text are now saved with a BOM, so
  Windows PowerShell 5.1 reads them correctly.

### Tests and CI

- **Script order:** `Test-SqlScriptOrder.ps1` fails the build when a script
  adds a column above the CREATE TABLE of its own table, which a fresh install
  skips; the retry columns of `dash.Extract` were the case it found.
- **Spelling:** `Test-Spelling.ps1` fails the build on a British spelling
  anywhere in the code, the SQL, the Power BI model or the guides, so 2.4.0's
  renames stay done.
- **Pages vs. guide:** `Test-OperationsGuide.ps1` checks all 61 report pages
  against the page table in `docs/DASHBOARD.md`, in both directions.
- **New checks:**
  - `Test-UserDirectory.ps1`: group expansion.
  - `Test-SqlIntegration.ps1`: group grants in `bi.OrgSecurity`, and stored
    Premium use.
  - The dashboard's unit tests: the retry policy.
- **Analyzer:**
  - CI runs PSScriptAnalyzer with `PSScriptAnalyzerSettings.psd1` and now fails
    on warnings as well as errors.
  - Rules that don't fit are excluded in that file, each with its reason.
- **CI actions:**
  - checkout v7
  - setup-python v7
  - setup-dotnet v6
  - upload-artifact v7
  - download-artifact v8

## [2.3.0] - 2026-09-27

The dashboard becomes something a Copilot programme can be run from: alerts and
digests by email and to Teams or ServiceNow, triage and investigations for
security and compliance, licence planning from budget to rollout waves, with
recommendations based on use that needs the Premium licence, and an admin page
for every setting. A ready-made Power BI model ships with the release, one
dashboard can span several Microsoft 365 tenants, and Copilot Studio's build
and publish activity is loaded with the licence events. The pipeline still
asks for the same three read-only Graph permissions.

### Setting up

The same two commands on the job server as in 2.2.0, then the dashboard and the
Power BI model:

```powershell
.\tools\Install-CopilotJobServer.ps1 -SqlServer SQLPROD01 -ServiceAccount 'CORP\gmsa-copilot$' `
    -TargetDomain users.contoso.com -TenantId contoso.onmicrosoft.com -RegisterApp -Dashboard
.\Invoke-CopilotPipeline.ps1 -History
# web server
.\dashboard\Install-CopilotInsights.ps1 -PackagePath .\copilot-insights-v2.3.0.zip -SqlServer SQLPROD01 `
    -AdminPrincipal 'USERS\Copilot-Insights-Admins' -GrantDatabaseAccess
```

Then open `powerbi\CopilotInsights\CopilotInsights.pbip` in Power BI Desktop and
set its two parameters. `docs/DEPLOYMENT.md` walks through all of it, and
`docs/OPERATIONS.md` covers everything after.

### Upgrading from 2.2.0

1. Back up the databases, and replace the scripts with this release's (keep `copilot-audit.settings.psd1` and `logs`).
2. Run `.\Install-CopilotAudit.ps1 -SqlServer <server> -Dashboard` once. It installs the new objects, catalogues the values of every record flattened before, and rebuilds the dashboard's rollups once with Premium use. Nothing else needs running.
3. Run the dashboard installer again with the new zip.
4. Replace a hand-built Power BI model with the one in `powerbi\`, or add the new `bi` views to yours.

From its first run, the nightly licence-event load also asks for Copilot Studio's authoring operations, and records the wider set of operations in the licence-event database. Authoring records from before the upgrade are not fetched.

### Added

**Operations**

- **Email** from the dashboard: Exchange Online with the mailbox's password or OAuth (`SMTP.SendAsApp`), or any SMTP server on any port with STARTTLS or implicit TLS and a user name and password. Passwords and secrets are encrypted by SQL Server and read only through procedures.
- **Alerts:** pipeline failures and stuck runs, stale data, the app certificate nearing expiry (30, 14 and 3 days), security signals against thresholds and 28-day averages, drops and spikes in a department's use, the licence pool running out, licences on disabled accounts, and agents used for the first time. Each goes to its own recipients, fires once per occurrence, and is written to the Windows Application log.
- **Webhook:** every alert also posted to Teams (an adaptive card) or as JSON for ServiceNow and the rest.
- **Digests:** scheduled Leadership, Adoption, Licensing, Security and Value digests (aggregates only); manager digests to every manager of their own organisation; and a weekly personal email anyone can ask for. Failed manager and personal sends are retried.
- **Scheduled extracts:** aggregate CSV files written to a folder or share.
- **A read-only data API**, `/api/v1`, with the pages' sign-in, scope and roles.
- **Privacy mode** (nobody named; Power BI's users pseudonymised) and **retention** of logs and stored JSON.

**Licensing and planning**

- **Premium use:** each licensed interaction is Premium (Copilot in Word, Excel, PowerPoint, Outlook or Teams, or Chat over work content) or free (web Copilot Chat, agents). *Train* and *revoke* now look at Premium use, so a licence used only for web Copilot Chat is flagged. **Use ranking** ranks everyone by general, free and Premium use.
- **Licence pool** (seats bought, assigned and used), **chargeback** by department and other attributes, and **licences on disabled or departed accounts**, from the licence tool's new reads of subscriptions, account state and user type.
- **Budget and renewal:** contracts, their cost and renewal dates, and the seats the use supports.
- **Rollout waves:** everyone without a licence, in order, split into waves with their own files.
- **Licence action outcomes:** every export tracked to whether it was applied, the licences reclaimed and whether new ones are used.
- Licence changes as an **Entra bulk-members file** or a **PowerShell script** for an administrator to apply.

**Security and compliance**

- **Triage** of flagged interactions: new, investigating, closed or false positive, with an owner and a note.
- **Guests and external users**, by home organisation.
- **Person record:** one person's full record and interactions, downloadable, for data-subject requests and investigations; open in privacy mode, and audited with whose record it was.
- **Oversharing hotspots:** the sites Copilot read from, by reach and sensitivity.

**Leadership, adoption and enablement**

- **Board pack** (printable), **use cases**, **value and time saved**, and **targets** with their progress.
- **Leaderboard** of departments, **new joiners' time to first use**, **drops and spikes**, **training effect** and **champion** suggestions.
- **Job families** from job-title rules.
- **Events** (rollout waves, training, outages) drawn on every trend chart.
- **Saved views** and a personal start page.

**Agents**

- **Build and publish:** Copilot Studio agents created, published, shared, changed and deleted, from Copilot Studio's own audit records (`audit.AgentEvent`, `bi.FactAgentEvent`), loaded with the licence events at no extra search cost.
- **Agent inventory** with owners and first use.

**Administration**

- Admin pages for **email**, **alerts and digests**, **targets and assumptions**, **privacy and retention**, **value names**, **classifications and events**, **scheduled extracts**, **training records** and **directory quality**; Health shows the app certificate and the audit searches waiting. Every setting is stored in SQL.
- **Value names:** a catalogue of every distinct value of 23 coded columns (capped at 10,000 each), named in the dashboard, by CSV or from PowerShell, and shown in Power BI.

**Several tenants**

- Each tenant keeps its own app registration and databases; `tools\Add-CopilotTenant.ps1` adds one, the nightly run loads each in turn, and the dashboard spans them with a tenant switcher and a group overview. The dashboard installer's `-Tenant` writes the list and grants access in every tenant's database.

**Power BI**

- **A ready-made model**, `powerbi/CopilotInsights`: a Power BI project over every `bi` view, with relationships, measures including Premium use and the three rankings, descriptions, and an *Organisation* row-level-security role from the org chart. Opened and refreshed in Power BI Desktop against the demo database, where its figures matched the dashboard's.
- New views: `AgentEvents`, `OrgSecurity`, `ValueNames`, `LicensePool`, `DepartmentWeek`, `UseCases`, `JobFamilies`, `EventMarkers`; `Interactions` gains `PremiumUse` and `UseCaseKey`.

**Documentation**

- `docs/OPERATIONS.md`, the operations guide, kept complete by `tests/Test-OperationsGuide.ps1`.

### Changed

- **Sign-in:** nothing in a URL changes who the viewer is (the Development-only view-as is removed), and a person with a SID on record can only be the account with that SID.
- **Charts show every label whole:** margins and label columns fit the labels, and long names wrap instead of being cut short; hover gives each mark's figures.
- The pipeline records each run and step (`audit.PipelineRun`), with the certificate it signed in with.
- Incremental loads accept more operations than their database recorded (never fewer), so a release can widen a load.

### Fixed

- **Privacy mode** could be undone in Power BI: `bi.OrgSecurity` paired real sign-ins with the pseudonymised users, and OneDrive site names and licence administrators were shown. All three are now hidden in privacy mode.
- *Remove* and other confirmed buttons in the admin pages did not ask before acting.
- The IIS installer now accepts the release's dashboard zip.

## [2.2.0] - 2026-09-27

Setting up is now two commands on the job server, with nothing typed into SQL
or clicked through in the Entra portal. The rest of the release: every field in
the audit record is flattened and carried into `bi`, a web dashboard for every
persona with forecasts and licence recommendations, and sign-in matching when
the web servers and the users are in different domains.

### Setting up

```powershell
.\tools\Install-CopilotJobServer.ps1 -SqlServer SQLPROD01 -ServiceAccount 'CORP\gmsa-copilot$' `
    -TargetDomain users.contoso.com -TenantId contoso.onmicrosoft.com -RegisterApp
.\Invoke-CopilotPipeline.ps1 -History
```

`docs/DEPLOYMENT.md` walks through both, step by step.

### Upgrading from 2.1.0

1. Back up both databases.
2. Replace the scripts with this release's.
3. Run `.\Install-CopilotAudit.ps1 -SqlServer <server>` once. It upgrades every object, flattens every record again (the flattened table gains columns), and rebuilds sessions and `bi` in full.
4. Refresh the Power BI model in full, and add the new `bi` views if you want them.
5. To move from a hand-written `Run-Nightly.ps1` to the shipped runner, run `tools\Install-CopilotJobServer.ps1` with the values that script used. It writes the settings file and re-registers the task.

### Added

- **One-command provisioning.**
  - `tools/Install-CopilotJobServer.ps1` sets up the job server: the certificate (self-signed, or yours by thumbprint), the service account's Read on its private key, *Log on as a batch job*, the gMSA check, connectivity to Entra and Graph, both databases, the app registration, a settings file (`copilot-audit.settings.psd1`) and the nightly scheduled task. It is idempotent: run it again with no parameters, and it redoes only what is missing, including renewing a certificate within 30 days of expiry.
  - `tools/New-CopilotAuditApp.ps1` registers the Entra app from PowerShell. It asks for the three Graph application permissions and nothing else, grants their admin consent and uploads the certificate, keeping any certificate already there.
  - `Invoke-CopilotPipeline.ps1` replaces the hand-written nightly script. It runs the four nightly steps from the settings file, each in its own process, stopping at the first failure. With `-History`, it loads and builds the history in one resumable command.
- **The reporting time zone is a setting.** `Install-CopilotAudit.ps1 -ReportingTimeZone` stores it in both databases (`audit.PipelineSetting`, read through `audit.ReportingTimeZone()`), and every procedure uses it. It replaces editing the default in five scripts. It is refused once records are loaded, so history can't be cut in two zones.
- **`Install-CopilotAudit.ps1`**: creates the databases, installs every object in dependency order, grants the service and reporting accounts, and builds whatever the data calls for (licence events, the first full build, the 2.2.0 re-flatten). **`tools/Invoke-CopilotMaintenance.ps1`**: status, rebuilds, app categories, roles, dashboard settings, agent and label names, and JSON retention, each written to the dashboard's audit log. Nothing day to day needs SQL typed by hand.
- **Every informative field of the audit record is flattened.** Typed columns are named after their JSON paths. Each accessed resource and each context item is a row of its own (`audit.RawLogAccessedResource`, `audit.RawLogContext`). Fields that never carry information are counted per combination in `audit.RawLogRecordShape`, and the flatten raises a note if one ever takes a second value. `OrganizationId` stays on every row for a multi-tenant future.
- **In `bi`:**
  - dimensions for licence type, sensitivity label, resource type, resource action and site;
  - `bi.FactAccessedResource`, one row per resource an interaction read, with its site and whether it was blocked;
  - on the interaction fact: licence type, label, message count, memory updates, deferred data-loss checks and agent-to-agent calls;
  - views: `AccessedResources`, `LicenseTypes`, `SensitivityLabels`, `ResourceTypes`, `ResourceActions`, `Sites`.
- **Forecasting and licence recommendations** in the dashboard. Weekly series are projected 13, 26 or 52 weeks ahead by damped-trend exponential smoothing, with 95% intervals. The recommendations are priced by a monthly licence cost set in Admin:
  - upgrade proven Copilot Chat users;
  - train light Premium users;
  - revoke licences unused for 30 days, and reassign them;
  - the right licence count for the next quarter.

  Each comes with the named list behind it, for the Licensing role.
- **More personas and reports:**
  - *My Copilot*, every employee's own use against the organisation's median;
  - licence changes: time held before removal, removals from people still using it, licences given back;
  - a leadership scorecard;
  - retention and habit cohorts;
  - signals and labeled content by unit;
  - agent hand-offs;
  - conversation depth;
  - governance controls;
  - regions and working patterns;
  - workforce attributes and managers' teams;
  - app owners' pages.

  All come from the same three Graph permissions and on-premises AD.
- **Web servers in one domain, users in another.** The directory load keeps each account's SID (`audit.UserDirectory.Sid`, `dash.Person.Sid`), and the dashboard matches a Windows sign-in by it. The users' domain isn't contacted at sign-in.
- **The top of the organisation is found.** With no `OrgTopUpn`, the dashboard uses the one person with no manager who has at least half the directory under them.
- **Copilot Insights, a web dashboard** (`dashboard/CopilotInsights`): ASP.NET Core 10
  Razor Pages in IIS on Windows Server 2022+, Windows sign-in, reading `bi` and a new
  `dash` schema. Pages for eight personas - Leadership, Adoption, Licensing, Security,
  Compliance, Agents, Enablement, Governance - with server-drawn charts, periods of 7,
  14, 30, 90, 180 or 365 days, and every list of people paged in SQL, searchable,
  sortable and exportable to CSV.
- **A security model from the org chart.** The top of the organisation (`OrgTopUpn`) and
  their direct reports see everything, as does anyone granted *Organization*; everyone
  else sees themselves and the people under them, at any depth, and can look inside
  their own part only. Named lists need a persona role and stay in scope. Groups smaller
  than five are folded together. The navigation and start page list only what the
  viewer can open. Every view, refusal, export and admin change is logged.
- **An admin module:** settings and the top of the organisation, role grants to people
  or AD groups, effective-access checks, agent and label names, app categories, pipeline
  health with a manual refresh, and the audit log. The first administrators come from
  the site's configuration (`-AdminPrincipal`), so there is always a way in.
- `sql/Create-CopilotDashboardSchema.sql` and `sql/Create-CopilotDashboardProcedure.sql`:
  the `dash` tables, `dash.fnScopeUsers`, `dash.Agent`, and `dash.usp_RefreshDashboard`,
  which rebuilds the org closure from `audit.UserDirectory` and the daily per-person
  rollups (`dash.UserDay`, `dash.ResourceDay`) from a given day. The backfill's
  `-Incremental` run and the CSV importer call it after `bi.usp_RefreshBi` when it is
  installed. The dashboard's account can run it without ALTER on its tables.
- `dashboard/Install-CopilotInsights.ps1`: IIS role services, the Hosting Bundle check,
  the site files, `appsettings.Production.json`, app pool and site, Windows
  authentication, and the database grants. Idempotent.
- `dashboard/demo/New-InsightsDemo.ps1`: a synthetic 2,317-person company built with the
  repository's own scripts, with one person per persona role, to try every page and the
  security model on.
- Tests: section 14 of `Test-SqlIntegration.ps1` (levels, the closure with a
  self-managed top, a manager loop and a missing manager, scope, rollups against the
  facts, labels and sites, incremental refresh, a refresh without ALTER), and
  `dashboard/CopilotInsights.Tests` for the access rules, paging, periods, CSV export and
  charts. CI builds, tests and publishes the dashboard.

### Changed

- **Flattening finds its batches faster.** The records to do are keyed once at the start, rather than found in the columnstore for each 200,000-record batch. At 40 million records that search was half the flatten, which now takes about 6½ hours instead of about 10.
- **Adoption can't pass 100%.** The Power BI starting-point measure `[Active licensed users]`, and the dashboard, count only people who still hold a licence at the end of the period (or of each week) and used Copilot while licensed. Before, someone whose licence was removed during the period counted as active and licensed, but not in `[Licensed users]`.

### Tested at 40 million interactions

70,000 people, 17,692 licensed, a year of records: flattening in 6 h 28 min, sessions in 32 min, `bi` in 43 min, the dashboard's rollups in 96 s; a nightly run from 48 hours back in about 7½ minutes. Counts agreed across `audit`, `bi` and the dashboard. Every dashboard page opened in under 9 seconds for 180 days, viewed as the top of the organisation, after fixes to the pages that took up to 105 seconds. `docs/DEPLOYMENT.md` has the figures and the space each table takes.

### Documentation

- `docs/DEPLOYMENT.md`: the 40-million-interaction test under *What has run in the lab*, and the space table, step 11's time and the nightly cost updated from it.
- `docs/DEPLOYMENT.md` leads with the two setup commands. Steps 2-4 and 12-13 now describe what the scripts do, with the manual alternatives for split teams. The certificate, private key, batch-logon right, scheduled task and time zone need no hand steps.
- `docs/DASHBOARD.md`: servers in one domain and users in another. Which trust direction is needed for what, how sign-ins are matched, grants from the users' domain, and Kerberos across a forest trust.
- `docs/DASHBOARD.md`: every page by persona, who sees what, periods, paging and
  exports, the IIS install, enrolling the first administrator, running it, and what is
  not yet proven. `docs/DEPLOYMENT.md` Phase 7 (steps 22-24), RUNBOOK section 5f, and
  the `dash` schema in `docs/SCHEMA.md`.
- `docs/SCHEMA.md`: every table and view, column by column - type, NULL,
  default, key, indexes and the DDL's own notes - with the fourteen scripts
  that create them, what each creates, where it runs and whether it runs by
  itself. Generated from the lab databases' metadata after the 2.1.0 upgrade.
  Linked from the README and `docs/DEPLOYMENT.md`.
- `docs/DEPLOYMENT.md`: the lab's upgrade from 2.0.0 to 2.1.0 (scripts,
  row counts, and the shared shredding function reproducing every existing
  interaction row), the CSV import test, a "new release" row under Running
  it, and the CSV importer on a real portal export under Not yet proven.

## [2.1.0] - 2026-09-25

History can now come from Purview's own audit log exports: point the
importer at a folder of CSV files, overlapping ranges and all, and the
records land exactly as the Graph backfill stores them - so a database can
start from exports, add history older than the API's 180 days, or both. And
a step-by-step deployment guide, `docs/DEPLOYMENT.md`.

### Upgrading from 2.0.0

Nothing to do by hand. The next `Invoke-CopilotAuditBackfill.ps1` run
installs the shared shredding function and the updated merge procedure, and
the CSV importer creates its own tables the first time it runs. New installs
follow `docs/DEPLOYMENT.md`.

### Added

- **Importing Purview CSV exports.** `tools/Import-CopilotAuditCsv.ps1 -Path <folder>`
  loads audit log exports from the Purview portal into `audit.CopilotAudit` and
  `audit.CopilotInteraction`, in the shape the Graph backfill stores, so the
  flattened table, sessions and `bi` work on them unchanged. Exports may
  overlap and repeat each other: one row per `RecordId` is kept, and a record
  already loaded wins. Files already imported are skipped by SHA-256; rows
  that can't be loaded (cut-off JSON from a file saved in Excel, no ID, no
  time) are listed in `audit.CopilotCsvReject`. The files are read by the
  importer, not SQL Server. A database can start from CSV files alone:
  `-CoveredThroughUtc` records the range, and `-Incremental` continues from
  it. `sql/Create-CopilotCsvImport.sql` holds the tables and
  `audit.usp_MergeCopilotCsv`. RUNBOOK section 5e. Tested on the lab's
  records reshaped to the export's format, not yet on a file downloaded from
  the portal: import one real export and check its summary first.

### Changed

- The JSON-to-`audit.CopilotInteraction` shredding lives in one inline
  function, `audit.fnShredCopilotInteraction` (in
  `sql/Create-CopilotAuditSchema.sql`), used by finalize, the incremental
  merge and the CSV merge. It was written out twice before; the output is
  unchanged.
- Finalize refuses once CSV imports are merged, as it does once incremental
  loads are: rebuilding from its staging table would drop them.
  `audit.CopilotAudit` gains `CsvImportId` for lineage, and
  `audit.CopilotBackfillRange` gains `Source` (`'CSV'` when an import recorded
  the range).

### Tests

- `tests/Test-CsvImport.ps1` (new, in CI): the reader against both export
  formats, quoted fields with commas, doubled quotes and line breaks, the
  Graph path's column mapping, the compressed JSON, rejected rows and their
  line numbers, and operation matching.
- `Test-SqlIntegration.ps1` section 13: overlapping exports of section 11's
  records into a new database give the same `audit.CopilotInteraction`, the
  same times, UPNs and JSON as the Graph path; re-running imports nothing; a
  new file adds only its new record and lists its cut-off row; the covered
  range is recorded and widened; finalize refuses afterwards; over the Graph
  path's own records nothing is added and its range is left alone; and an
  unfinalized backfill is refused.

### Documentation

- `docs/DEPLOYMENT.md`: a step-by-step deployment guide for a new install -
  decisions to make first, the app registration and certificate, the job
  server and SQL Server, the history load, the AD directory and licences, a
  nightly script and its scheduled task, the Power BI model, and Copilot in
  Power BI (tenant settings, model descriptions, AI instructions, verified
  answers). It records what has run in the lab and what hasn't yet run
  anywhere. Linked from the README.

## [2.0.0] - 2026-09-24

User attributes now come from on-premises Active Directory, with Graph used
for licences only - three Graph permissions instead of four, and none for
profiles. And the nightly load stops redoing all of history: at 40M
interactions it went from about 31 minutes to about 74 seconds, with
identical results.

### Setting up

The app registration needs three Microsoft Graph application permissions:
`AuditLogsQuery.Read.All`, `User.Read.All` and `LicenseAssignment.Read.All`.
Schedule three jobs daily, in this order:
`tools/Export-UsersWithHierarchy-ToSql.ps1` (on a machine that reaches the
trusted domain; check the match percentage it reports on the first run),
`tools/Update-CopilotAuditEnrichment.ps1`, then
`Invoke-CopilotAuditBackfill.ps1 -Incremental`. README and RUNBOOK
(sections 5b-5d) have the details.

A database built by 1.x upgrades in place: re-run the enrichment, session
and `bi` scripts in `sql/` (the procedures refuse to run until then), load
the user directory, and build sessions once with no `@SinceUtc`.

### Added

- **Incremental sessions.** `audit.usp_BuildCopilotSessions @SinceUtc` rebuilds
  only from local midnight of that day: a session never crosses midnight,
  so nothing earlier can change. Those sessions are replaced, each stream's
  numbering carries on from its last kept session, and every week containing
  a rebuilt day is recomputed from `audit.CopilotSession` (so a week that
  straddles the day counts its kept sessions). The result equals a full
  rebuild over the same rows, except that kept sessions keep the directory
  attributes and licence flag they were built with. `-Incremental` passes
  the load's start; an incremental build is refused if the last build used
  different settings. `audit.CopilotSessionRun.RebuiltFrom` records the day
  (NULL for a full build).
- **`bi.FactSession` follows the session builds** instead of being rebuilt
  whole on every refresh: the days incremental builds rebuilt, or every
  session after a full build renumbered them (`bi.RefreshRun.SessionRunId`).

### Fixed

- `bi.usp_RefreshBi` scanned all of `audit.RawLogFlattened` on every partial
  refresh: once to find its window (an `@SinceUtc IS NULL OR ...` predicate
  that cannot seek), and again to read it (a plan cached from the first,
  month-sized full build). Both now seek.
- `bi.FactLicensedDay` is built by turning each licence period into its range
  of days, instead of testing every date against every period.

### Performance

Measured on 40M synthetic interactions (70,000 users, 18,000 licensed, a year
of data; 8 cores, 31 GB RAM):

| Nightly step | Before | After |
|---|---|---|
| Sessions and weekly rollup | 1,697 s (full rebuild) | 44-47 s (last 48 h) |
| `bi` 48-hour refresh | 158 s | 27 s |
| Largest single statement's tempdb (sessions) | 11.9 GB (23.6 GB in all) | 1.9 GB |

An incremental build over a full one left all 7,382,589 sessions and
5,545,894 weekly rows identical. The nightly cost now follows the window,
not the history kept. The new indexes on `audit.CopilotSession` take 0.75 GB
at that size.

### Changed (breaking)

- **User attributes come from on-premises AD, not Entra.** The new
  `tools/Export-UsersWithHierarchy-ToSql.ps1` reads every user in a trusted
  domain and replaces `audit.UserDirectory` in one transaction: display
  name, mail, employee type, title, department, division, office, city,
  country, company, manager, whether they manage anyone, whether the account
  is enabled (every account is loaded; there is no enabled filter), and the
  management chain from the top down (`Mgr1`..`Mgr15`, `OrgDepth`).
  Sessions, `bi.DimUser` and the adoption views read it instead of
  `audit.UserProfile`. It binds as the identity running it, needs no Graph
  permission, and runs on Windows PowerShell 5.1 or PowerShell 7. After
  loading it reports how many audit users it matched by UPN, and by mail.
- **`tools/Update-CopilotAuditEnrichment.ps1` reads licences only.** No
  profiles, managers or administrative units, so it no longer writes
  `audit.UserProfile` or `audit.AdministrativeUnit` (left in place on
  existing databases; nothing reads them). Its default scopes are
  `User.Read.All` and `LicenseAssignment.Read.All`; `-SkipProfiles` is
  accepted and ignored.
- **The app registration needs three permissions, not four:**
  `AuditLogsQuery.Read.All`, `User.Read.All`, `LicenseAssignment.Read.All`.
  Remove `Organization.Read.All` and `AdministrativeUnit.Read.All`.
- **`bi.DimUser` (`bi.Users`)** gains `EmployeeType`, `IsManager`,
  `ManagerName`, `OrgDepth` and `OrgLevel1`..`OrgLevel15` - the org chain as a
  ragged hierarchy for the model - and loses Entra's `UserType`. Re-run
  `sql/Create-CopilotBiSchema.sql` to upgrade in place; the procedure refuses
  to run against the older table. `DirectoryStatus` is `Found` for users in
  AD.
- `audit.vCopilotAdoption` returns `Division`, `Office`, `EmployeeType` and
  `AccountEnabled` from AD in place of `OfficeLocation` and `UserType`.

### Tests

- `tests/Test-UserDirectory.ps1` (new, in CI): the chain from the top down,
  a chain through a disabled manager and through a manager of a type not
  loaded, loops and missing managers, the type filter and map, disabled
  accounts loaded and flagged, skipped rows counted, `-MaxLevels`. Runs on
  Windows PowerShell 5.1 and PowerShell 7.
- `Test-SqlIntegration.ps1` section 8: the licence tool calls only
  `/subscribedSkus` and `/users/{upn}` and creates no Entra profile table;
  the AD rows load through the tool's own writer, a second load replaces the
  first, the match report counts UPN and mail matches, and the adoption
  views take departments from AD. Section 12 checks the org chain reaching
  `bi.DimUser`.
- `Test-SqlIntegration.ps1` sections 10 and 12, incremental sessions: after
  a new prompt, an incremental build equals a full rebuild column for column
  (sessions and weeks, including a week straddling the rebuilt day); earlier
  sessions keep their keys; numbering carries on; a build under different
  settings is refused; and `bi.FactSession` stays equal to the sessions
  through incremental, full and no session builds.

## [1.6.0] - 2026-09-24

A star schema for a Power BI semantic model, kept current by every
incremental load.

### Added

- **The `bi` schema: a star schema for a Power BI semantic model.**
  `sql/Create-CopilotBiSchema.sql` and `sql/Create-CopilotBiProcedure.sql`
  add four facts - interactions, sessions, licence changes, and licensed
  user-days for the adoption denominator - and their dimensions (date with
  the Saturday-Friday week, user, app with its category, agent, model, SKU,
  licence status), with integer keys that stay stable across refreshes. A
  model imports the views (`bi.Interactions`, `bi.Users`, `bi.Calendar` ...),
  which leave out lineage; the columns that are one value per row (RecordId,
  ThreadId, resources, URLs, labels, JSON) are left in `audit.*`.
  - `bi.usp_RefreshBi` rebuilds the interactions from `@SinceUtc`, a month at
    a time, and the smaller facts whole. `-Incremental` calls it after the
    other derived tables, with the load's start.
  - Each interaction carries the licence held at the time: from the licence
    history (`@LicenceEventDatabase`, the same rule as sessions), else from
    the enrichment's `LicensedSinceUtc`, else Unknown.
  - `ActivityHour`, local time to the hour, is the column for the model's
    incremental refresh.
  - A partial refresh in a different reporting time zone from the last one is
    refused; it would mix zones in one table.
  - The agent dimension reads `CopilotEventData.AgentId` when a record has
    one, and otherwise the agent named in `AppIdentity`
    (`Copilot.Studio.<env>-<tenant>-<schema name>`, `MicrosoftAgent.<...>`).
    On a live tenant AgentId was empty on every row, Copilot Studio agents
    included, so without this every agent interaction would have read
    "(no agent)". `AgentSource` says which it came from.

### Tests

- `Test-SqlIntegration.ps1` section 12 builds the schema over sections 8-10's
  data: every key resolves; licence at the time of use from history, from
  `LicensedSinceUtc`, and Unknown; local dates and hours either side of
  midnight; the calendar's week; sessions, licence changes and licensed days;
  a partial refresh rewrites its window only and keeps keys stable; a full
  rebuild re-registers nothing; and the time-zone and licence-database
  refusals. It also covers agents named only in `AppIdentity` (Copilot Studio
  and Microsoft), an `AgentId` taking precedence, and the schema script
  upgrading an older `bi.DimAgent` in place - the path a fresh test database
  never takes, which failed on the first live install.

## [1.5.0] - 2026-09-24

Incremental loads: a finalized database kept current on a schedule, so a
semantic model refreshing daily has something new to refresh.

### Added

- **`-Incremental`: keep a finalized database current on a schedule.** The
  backfill covers one fixed range, and finalize rebuilds everything from it;
  nothing kept the tables current after that, so a semantic model refreshing
  daily had nothing new to refresh. Each `-Incremental` run loads from where
  the data ends - the last load's end, or the backfill's - up to the start of
  the current hour, and appends instead of rebuilding.
  - Every load re-searches the `-LookbackHours` (default 48) before its start.
    Microsoft commits to no delivery time for audit records (core services
    typically 60-90 minutes), so a record that arrives late is picked up by the
    next load. Records already present are counted, never duplicated.
  - The engine runs unchanged against its own `audit.CopilotLoadWindow` and
    `audit.CopilotLoadStage`; `audit.CopilotLoad` logs each load. A run that
    stops part-way resumes the same load next time, and a run with failed
    windows exits non-zero rather than leaving a silent hole.
  - `audit.usp_MergeCopilotLoad` (`sql/Create-CopilotLoadProcedure.sql`,
    installed automatically) checks the load covers its range with no gap, then
    appends the new records to `audit.CopilotAudit` (with a `LoadId` for
    lineage, added in place on older databases) and `audit.CopilotInteraction`
    in one transaction, and empties the load tables.
  - Afterwards it refreshes whichever derived tables are installed: the
    flattened table and licence events from the load's start, then sessions
    (`-SessionLicenceDatabase` passes the licence history). `-SkipPostProcessing`
    stops after the merge.
- **App-only sign-in** with `-ClientId` and `-CertificateThumbprint`, so a
  scheduled task can run with nobody to sign in. Needs the Microsoft Graph
  application permission `AuditLogsQuery.Read.All`.
- RUNBOOK section 5b: the app registration and the scheduled task, with every
  permission one app registration needs to run all of it. All are Microsoft
  Graph application permissions, admin-consented and read-only:

  | Permission | Needed for |
  |---|---|
  | `AuditLogsQuery.Read.All` | The backfill and `-Incremental` loads (Copilot records and licence events). Enough on its own without the enrichment tool. |
  | `User.Read.All` | Enrichment: profiles, Division, Copilot licences, managers. |
  | `Organization.Read.All` | Enrichment: finding the Copilot SKUs (`LicenseAssignment.Read.All` also works). |
  | `AdministrativeUnit.Read.All` | Enrichment: administrative-unit names. |

  Sign-in is by certificate. The app needs no Purview audit role; that applies
  to people signing in.

### Changed

- `-Finalize` refuses once an incremental load has been merged: it rebuilds from
  the backfill's staging table only, and would drop every day the loads added.
- `-Status` lists the latest incremental loads.

### Tests

- `Test-SqlIntegration.ps1` section 11 runs the real engine: a backfill of five
  days, finalized, then a load over four more with a day of lookback. Every
  record ends up present exactly once, the lookback day is counted as already
  loaded, a record removed to simulate late delivery is added back by the next
  load and nothing else is, merging with nothing waiting changes nothing, and
  finalize, an unfinished load and a load with a coverage gap are all refused.
- `Test-Orchestrator.ps1` section 2b covers the load range: the lookback, the
  whole-hour end, an explicit end, and nothing to load.

## [1.4.0] - 2026-09-24

Sessions and the weekly rollup in SQL, licence history that carries its
detail, and a planner that sizes windows from what it actually loads.

### Added

- **Inferred sessions and the Saturday-Friday weekly rollup, in SQL.** A port
  of the session script that read the Purview CSV export (v5.7.5).
  `sql/Create-CopilotSessionSchema.sql` and `sql/Create-CopilotSessionProcedure.sql`
  add `audit.usp_BuildCopilotSessions`, which rebuilds `audit.CopilotSession`
  (one row per session) and `audit.CopilotWeeklyRollup` (per week, user,
  primary app, category and licence) from `audit.RawLogFlattened` in one
  transaction. `audit.vCopilotSessionSummary` and `audit.vCopilotWeeklyRollup`
  carry the old CSVs' exact column names and formats.
  - The script's rules: a new session after 60 minutes idle or on a new
    calendar day in the reporting time zone; primary app and licence from the
    first prompt; weeks run Saturday to Friday, labeled by the Friday, week 1
    ending on the year's first Friday; a session counts in the week of its
    last prompt.
  - Two settings where the Graph data differs from the CSV. The script's
    'Copilot Chat' test matched the portal export's App label; the Graph data
    has host names, so `audit.SessionAppCategory` maps them (`Office`,
    `OfficeCopilotSearchAnswer`, `BizChat`, `Edge`) and is editable. And the
    script keyed sessions on app context, falling back to Location, skipping
    rows with neither: on a live tenant that skipped every Copilot Chat,
    Outlook and Teams row, 507 of 1,145. The procedure keys on the user by
    default; `@ContextFallback = 'Location'` reproduces the script exactly.
    `audit.CopilotSessionRun` logs each build's rule and every skipped row.
  - `@LicenceEventDatabase` (one or several, comma-separated) sets each
    session's licence flag to the licence held when it began, from the
    licence-event history, rather than today's. On the live tenant this
    changed the flag on 65 of 240 sessions.
  - 240 sessions and 128 weekly groups from 1,145 records in 0.7 s.
- `tools/Export-CopilotSessionCsv.ps1` writes `copilot_session_summary.csv` and
  `weekly_rollup.csv` from those views, quoted and UTF-8 as before, streamed
  from SQL and renamed into place when complete. `-Build` rebuilds first.
- `audit.vLicenseSkuChange` diffs each licence change's old and new
  `AssignedLicense` lists into one row per SKU added or removed, which is the
  question the licence-event feature exists to answer: when was this user
  given Copilot, and when was it taken away.
- **Division** in `audit.UserProfile`, from Entra's `employeeOrgData.division`,
  and in the session outputs. Existing databases are altered in place.
### Fixed
- **Windows now grow on a fresh run when the service reports no record count.**
  The planner learned density only from `approximateReturnedRecordCount` on the
  search status, and a live tenant returned none for any search, Copilot or
  Entra. Density stayed unknown, every window was planned at
  `InitialWindowHours`, and a 180-day licence-event backfill submitted one search
  per day, heading for the submission budget. The 1.2.0 empty-window fix looked
  verified live only because that run had been restarted: a resume reads the last
  loaded window's `RowsLoaded`, which a fresh run never did. A loaded window now
  feeds its actual row count back to the planner whenever the status carried no
  approximate count. The write happens on a SQL writer task, so it is volatile.

- **Licence changes now carry their detail: most of them arrive chunked.**
  1.3.0 assumed a detail-free `Change user license` had an `Update user`
  sibling holding the licence lists. On a live tenant (1,042 directory records
  over 180 days) that was not the shape: 400 of 422 `Change user license`
  records had no detail, and none was recovered by pairing. When a change is
  too large for one record - anyone with a few SKUs, because `AssignedPlan`
  lists every service plan - Entra splits it across up to seven records, each
  with empty `ModifiedProperties` and an `additionalDetails` of
  `{"id","seq","b"}`. The `b` fragments, joined in `seq` order, are one JSON
  document whose `targetUpdatedProperties` holds the change. 775 of the 1,042
  records were fragments.
  - `audit.usp_ShredLicenseEvents` keeps the fragments in a new
    `audit.LicenseEventChunk` table (a group can span shredder batches),
    reassembles every complete group after the batches, and files its
    properties against the `seq = 1` record with `Source = 'chunks'`. A group
    missing a fragment is reported and kept for a later run, never parsed from
    a partial document. On the live data: 152 of 152 groups, 760 properties.
  - Fragments are 10,000 characters, so they are read with
    `OPENJSON ... WITH (... NVARCHAR(MAX))`: `JSON_VALUE` returns NULL past
    4,000.
- **`audit.vLicenseChange` returns licence changes only.** It returned every
  directory event - 998 of its 1,042 rows had no licence detail, most of them
  `Update user` events for things like Authenticator devices. It now returns
  one row per licence property per change: each chunk group once, on its
  `seq = 1` record; every licence property rather than `TOP (1)`; and the
  `Update user` that Entra raises alongside each licence change, with the same
  detail and `InterSystemsId`, folded into the `Change user license`. Failed
  assignments (`MethodExecutionResult` only) are no longer reported as
  changes. The live tenant's 1,042 rows become 98 changes. Pairing is kept as a
  fallback and never borrows from the record's own chunk group.
- **`audit.vLicenseLifecycle` counts a change once.** It counted every
  fragment, so one user showed 24 licence changes rather than 4. It also
  matches `Add user` and `Delete user` exactly, because the service's operation
  filter for `Add user` returns `Add user sponsor` too, which is not an account
  being created.


### Tests

- `mock_graph.py` gains `report_approx_count` (default on) to withhold
  `approximateReturnedRecordCount`. `Test-Engine.ps1` section G runs a sparse
  30-day tenant without it: 30 one-day searches before the fix, 16 after, with
  windows reaching 168 h.
- `Test-SqlIntegration.ps1` section 10 puts fixture rows on each session rule's
  edge (59 and 60 minutes, midnight, a Saturday, a row with no context and
  one with no user) and checks sessions, weeks and the run log under both the
  default and the script's rule, plus the licence held at each session's start.
  Section 8 checks Division.
- `Test-SqlIntegration.ps1` section 9b reproduces the live shapes: a change
  chunked into three fragments inserted out of order, its chunked
  `Update user` twin, an Authenticator update, a failed assignment, a group
  missing a fragment and an `Add user sponsor`. Section 9's pairing assertion
  now expects every licence property from the pair, not one.
- The suite runs against a local instance with Windows authentication
  (`SQLIT_INTEGRATED=1`), and its `GO` splitter handles CRLF, so it passes
  from a Windows checkout rather than only on the Linux CI runner.

## [1.3.0] - 2026-09-24

### Added

- **Licence lifecycle from the audit log, not from the directory.** Microsoft
  Graph reports only the *current* licence assignment, so "when was this user
  given Copilot" is answerable and "when was it taken away" is not. The
  unified audit log carries both, as Entra directory events, for 180 days.
  Two new scripts read them:
  - `sql/Create-LicenseEventSchema.sql` — `audit.LicenseEvent` and
    `audit.LicenseEventProperty` for the shredded events, `audit.vLicenseChange`
    for one row per licence change, and `audit.vLicenseLifecycle` for one row
    per user with the provisioning and revocation dates.
  - `sql/Create-LicenseEventProcedure.sql` — `audit.usp_ShredLicenseEvents`,
    batched and MERGE-based like the flatten procedure, so it is safe to run
    incrementally.
- Collect the source records by adding the operations to a backfill run:
  `-Operations 'Change user license','Add user','Delete user','Update user'`.
  `-Operations` was already a passthrough to the API's `operationFilters`; no
  engine change was needed.
- Two details that cost real debugging time, handled in the shredder rather
  than left to whoever queries the tables. Entra writes the same operation
  both with and without a trailing period (`Change user license.`), which is
  normalised away. And a `Change user license` record often carries no licence
  detail at all — its detail sits on a sibling record written in the same
  transaction, so the shredder pairs records on `InterSystemsId` and marks the
  borrowed rows `FromPairedRecord = 1` rather than silently merging them.
- Target resolution prefers the UPN on the event itself and falls back to the
  paired record's, so a rename between the event and the report does not lose
  the user.

### Tests

- `tests/Test-SqlIntegration.ps1` gains section 9: the trailing-period
  normalisation, the licence property on both carrying records, the
  detail-free record picking up its licences from its pair, the new licence
  value being readable, and `vLicenseLifecycle` reporting both provisioning
  and revocation for one user.

## [1.2.0] - 2026-09-24

### Added

- **Directory enrichment**, so adoption can be measured rather than only
  counted. `tools/Update-CopilotAuditEnrichment.ps1` reads Microsoft Graph and
  writes only to its own lookup tables, on its own schedule, and cannot touch
  the audit archive:
  - `audit.CopilotLicense` — who holds a Copilot licence, which SKU, since when
    (`assignedPlans[].assignedDateTime`) and its capability status.
  - `audit.AdministrativeUnit` — administrative unit id to display name, which
    is what makes the GUIDs in `audit.CopilotAudit.AdministrativeUnits` readable.
  - `audit.UserProfile` — department, job title, office, city, country, usage
    location, member or guest, account enabled, manager and creation date.
  - `audit.vCopilotAdoption` — licensed users against actual usage, including
    `NeverUsed` and `DaysToFirstUse`. The shelfware view.
  - `audit.vCopilotUsageByDepartment` — usage per department, day and app.
- It looks up only the users that appear in `audit.CopilotInteraction` rather
  than the whole directory; `-AllLicensedUsers` adds licence holders who have
  never interacted, which the shelfware report needs. `-SkipProfiles` limits it
  to licences and units. App-only sign-in is supported with
  `-ClientId` and `-CertificateThumbprint`, so it can run unattended.
- Two limits are recorded rather than papered over. Graph gives the date the
  *current* licence assignment began, so "licensed since March" is answerable
  and "was this user licensed last March" is not. And audit records outlive
  directory objects, so a departed user is stored with
  `DirectoryStatus = 'NotFound'` and `IsCopilotLic` left NULL — "not known",
  which is a different claim from "unlicensed" and avoids understating
  licensed usage.

### Fixed

- **An empty search window no longer shrinks the next one back to a day.** The
  planner treated a window that returned no records the same as having no
  measurement yet, so every empty window reset the next to
  `InitialWindowHours`. On a sparse tenant, and across every day past the
  180-day retention horizon, a 365-day run submitted one search per day and
  hit the 150-per-24h budget part-way through, pausing for up to a day. An
  empty window now widens the next one, still at most doubling per step and
  capped at `MaxWindowHours`. On a live tenant the windows went
  24 → 48 → 96 → 168 h, so a year needs about 75 searches rather than 365.
- The warning about ranges longer than the 180-day retention printed a literal
  `{0:N0}` instead of the day count.
- **`-ConnectionString` never worked, in any release up to 1.1.1.** It threw
  `Keyword not supported: 'ConnectionString'` before anything ran.
  `SqlConnectionStringBuilder` is a `DbConnectionStringBuilder`, so PowerShell
  routes an assignment to `.ConnectionString` through its keyword indexer
  rather than the CLR property, and the indexer rejects that name. The
  constructor overload is the only form that works. The parameter is optional
  and no test exercised the branch, which is why it survived three releases;
  `Test-Orchestrator` now covers both branches of `New-SqlConnectionString`.

### Changed

- `audit.CopilotLicense.IsCopilotLic` is nullable. It shipped as `BIT NOT NULL`
  from before there was a third answer to record. Existing databases are
  altered in place by `sql/Create-CopilotEnrichmentSchema.sql`.
## [1.1.1] - 2026-09-23

### Changed

- **`audit.usp_FlattenCopilotAudit` is about 8x faster.** Measured on the
  integration suite: 168,000 records in 52 s rather than 413 s, so roughly
  3,200 records/s rather than 407. A 180-day 50M-record backfill goes from about
  34 hours to about 4.

  It used to read twenty-odd fields with twenty-odd `JSON_VALUE` and `OPENJSON`
  calls, and each of those parses the whole document. Each batch now parses in
  stages and keeps the results: one `OPENJSON` over the document for the
  top-level scalars and the `CopilotEventData` fragment, a second over that
  fragment for its scalars and the nested arrays, and `AccessedResources`
  exploded once into a temp table. Everything after that, including the
  `DISTINCT` lists `STRING_AGG` cannot express directly, is ordinary relational
  aggregation over rows that are already parsed.

  Temp tables rather than CTEs on purpose: SQL Server does not materialise a
  CTE, so referencing one repeatedly re-runs the `OPENJSON` underneath it.

  Output is unchanged. The one visible difference is ordering within the
  semicolon-separated lists: the resource name, location and blocked-URL lists
  now share a single ordering, so the Nth entry of each refers to the same
  resource, which the previous per-subquery orderings did not guarantee.

## [1.1.0] - 2026-09-23

### Added

- **Offline module support, for servers with no PowerShell Gallery access.**
  `Invoke-CopilotAuditBackfill.ps1` now puts a `Modules` folder sitting beside it
  at the front of `PSModulePath` at startup. Nothing is installed on the server
  and nothing is written outside the tool's folder; when the folder is absent,
  module resolution is unchanged.
- `tools/Save-CopilotAuditModules.ps1` builds that folder on any machine with
  gallery access: it pins `Microsoft.Graph.Authentication` 2.40.0, verifies the
  saved copy resolves with `PSModulePath` pointing at the bundle alone (so a
  copy already installed on the build machine cannot mask a broken bundle), and
  writes `MODULES-MANIFEST.csv` with a SHA-256 for every file.
- Releases now also carry `copilot-audit-backfill-<version>-offline-win-x64.zip`
  and its checksum, built on a Windows runner by running that same script. It
  includes `NOTICE-BUNDLED-MODULES.txt` recording what is bundled, its version,
  its source and its licence.

### Changed

- The "`Connect-MgGraph` not found" error now points at the offline bundle when
  no `Modules` folder is present, and says the bundle looks incomplete when one is.

`Microsoft.Graph.Authentication` is the only external module; everything else the
tool uses, including `System.Data.SqlClient`, ships inside PowerShell 7.

## [1.0.0] - 2026-09-23

First release.

### Backfill pipeline

- Pulls `CopilotInteraction` audit records for any date range from the Microsoft
  Purview Audit Search Graph API into SQL Server. Audit searches, page downloads
  and SQL loading run concurrently, with no CSV files in between.
- **Adaptive windowing.** Each audit search is sized from observed record density
  toward `-TargetRecordsPerWindow` (default 600,000), well inside the service's
  ~1,000,000-record-per-search cap. A search the service reports as over the cap
  (`isRecordCountLimitExceeded`) is split *before* anything is downloaded; where
  that flag is unavailable, a capped result is detected by row count instead.
- **Stays inside the published limits.** Caps itself at 150 search submissions per
  rolling 24 h against the tenant-wide ~200 (`-MaxSubmissionsPer24h`, counted
  across restarts), 8 searches in flight against the ~50 allowed, and honours
  `Retry-After` on every 429.
- **Adaptive download concurrency.** Starts at 4 streams, adds one after a run of
  clean pages, halves on a 429 and pauses every stream for `Retry-After` (AIMD).
- **Parallel bulk loading.** `SqlBulkCopy` with `TABLOCK` into a heap staging
  table, so several writers take bulk-update locks and load concurrently with
  minimal logging.
- **Resumable.** Every window's state lives in `audit.CopilotBackfillWindow`, so
  Ctrl+C, a crash or a reboot costs only the windows in flight. Searches that
  already completed server-side are reused rather than resubmitted. A window
  re-downloaded after an interruption gets a new attempt number, and finalize
  keeps only each window's final attempt, so a partial load never double-counts.
- **Token handling.** Silent refresh through the Graph SDK, and a clean stop if a
  Conditional Access policy forces a re-sign-in that nobody answers.
- **Compressed at the source.** Each record's JSON is gzipped to the exact bytes
  SQL Server's `COMPRESS()` produces for `NVARCHAR`, so
  `CAST(DECOMPRESS(AuditDataGz) AS NVARCHAR(MAX))` reads it back. Microsoft's
  documented sample records compress 2.4x–7.2x.
- The engine is C# compiled in memory at start — no binaries are shipped.

### SQL

- `sql/Create-CopilotAuditSchema.sql` — the control tables and the heap staging
  table. Idempotent; run automatically on every start.
- `sql/Finalize-CopilotAudit.sql` — refuses to run unless every window is loaded
  with no gaps, then builds `audit.CopilotAudit` (clustered columnstore, boundary
  duplicates removed), the slim `audit.CopilotInteraction` reporting table,
  `audit.vCopilotDailyUsage`, and `audit.vCopilotAuditRecord` in the Purview
  export's column shape. Re-runnable.
- `sql/Create-CopilotFlattenedSchema.sql` — `audit.RawLogFlattened`, one row per
  record with `CopilotEventData` shredded into columns, plus the optional
  `audit.CopilotLicense` lookup that fills `IsCopilotLic`.
- `sql/Create-CopilotFlattenProcedure.sql` — `audit.usp_FlattenCopilotAudit`.
  Shreds accessed resources and their locations, actions, sensitivity labels,
  plugins, contexts, agents, models, and the `JailbreakDetected` and
  `XPIADetected` security flags. Batched and MERGE-based, so it is safe to run
  incrementally on a schedule.
- `sql/Create-CopilotPurgeProcedure.sql` — `audit.usp_PurgeCopilotAuditJson`.
  Retention for the compressed JSON, which is what costs the disk: by default it
  clears `AuditDataGz` past the horizon and leaves the reporting and flattened
  rows intact; `@DeleteRows = 1` removes the records instead. `@WhatIf` defaults
  to 1 and only reports.

### Tests

- `tests/Test-Engine.ps1` — the engine end to end against `tests/mock_graph.py`,
  a mock of the Audit Search API: adaptive windowing, cap detection and splits, a
  server-side search failure, page 429s under a concurrency ceiling, random 503s,
  token rotation, transient SQL failures, a hard stop mid-run and a resume. Proves
  every record lands exactly once in the finalized set.
- `tests/Test-Orchestrator.ps1` — the PowerShell orchestrator's own logic,
  including a real `PipelineStoppedException` in a separate runspace.
- `tests/Test-SqlIntegration.ps1` — the real `SqlWindowStore`, `SqlBatchSink` and
  every SQL script against a real SQL Server: creates the database at
  compatibility level 160, backfills 8 days through the engine, stops and resumes
  it, finalizes, and checks row counts, duplicates, `ISJSON` on every decompressed
  payload, the flattened columns and both purge modes. Also proves finalize is
  re-runnable and that it refuses a range that is not fully loaded.

### CI

- `.github/workflows/ci.yml` — the engine and orchestrator suites on
  windows-latest and ubuntu-latest, PSScriptAnalyzer (failing on errors, reporting
  warnings), and the SQL integration suite against a SQL Server 2022 service
  container.
- `.github/workflows/release.yml` — runs every suite, then publishes the release
  archive and its SHA-256 checksum on a `v*` tag.

[2.4.3]: https://github.com/JosephSaad/copilot-audit-backfill/compare/v2.4.2...v2.4.3
[2.4.2]: https://github.com/JosephSaad/copilot-audit-backfill/compare/v2.4.0...v2.4.2
[2.4.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v2.4.0
[2.3.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v2.3.0
[2.2.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v2.2.0
[2.1.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v2.1.0
[2.0.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v2.0.0
[1.6.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.6.0
[1.5.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.5.0
[1.4.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.4.0
[1.3.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.3.0
[1.2.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.2.0
[1.1.1]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.1.1
[1.1.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.1.0
[1.0.0]: https://github.com/JosephSaad/copilot-audit-backfill/releases/tag/v1.0.0
