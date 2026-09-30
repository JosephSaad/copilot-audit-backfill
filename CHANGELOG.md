# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.4.1] - Unreleased

Fixes found in a first production install of 2.4.0, and the guides that install
would have needed. Install over 2.4.0: replace the scripts and run `Install-CopilotAudit.ps1`
once, which widens one `bi` column in place (below). No data is reloaded.

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
  web server needs before the dashboard's installer. Release links point at 2.4.0.
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
  - signals and labelled content by unit;
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
    first prompt; weeks run Saturday to Friday, labelled by the Friday, week 1
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

[2.4.1]: https://github.com/JosephSaad/copilot-audit-backfill/compare/v2.4.0...main
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
