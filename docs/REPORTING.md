# Reporting on the Copilot data: the brief guide

For a team building its own reports on the Copilot audit databases, in Grafana or any other tool that reads SQL Server. It says what is in the databases, which objects to read, how to join them, how the main figures are defined, and how to set Grafana up.

This is the short version, about ten pages. **[REPORTING-REFERENCE.md](REPORTING-REFERENCE.md) is the detailed one**: every table and view column by column, how each is filled, more queries, and the full text of the SQL and PowerShell scripts that create the schema. Each section below names the part of the reference that continues it. Both are also Word documents, beside these files: `REPORTING.docx` and `REPORTING-REFERENCE.docx`.

Written for release 2.6.2. The SQL here was run against a demo database of that release, with Grafana's macros replaced by fixed dates. It was not run inside Grafana.

## Contents

1. [What is in the databases](#1-what-is-in-the-databases)
2. [How the data gets there, and when](#2-how-the-data-gets-there-and-when)
3. [What to read: the `bi` views](#3-what-to-read-the-bi-views)
4. [Getting access](#4-getting-access)
5. [Rules every query follows](#5-rules-every-query-follows)
6. [The main figures](#6-the-main-figures)
7. [Grafana: the data source](#7-grafana-the-data-source)
8. [Grafana: time, variables and annotations](#8-grafana-time-variables-and-annotations)
9. [Queries to start from](#9-queries-to-start-from)
10. [Privacy and who sees what](#10-privacy-and-who-sees-what)
11. [Keeping reports fast](#11-keeping-reports-fast)
12. [Where to look next](#12-where-to-look-next)

## 1. What is in the databases

The product loads Microsoft 365 Copilot's audit records from Microsoft Purview into SQL Server and builds reporting tables from them. One Microsoft 365 tenant has two databases:

| Database | Default name | Holds |
|---|---|---|
| Copilot database | `CopilotAudit` | Every Copilot interaction record, and everything built from them. **Reports read this one.** |
| License-event database | `CopilotLicenseEvents` | Directory audit records about licenses and users, and Copilot Studio's authoring records. The Copilot database copies what reports need from it each night. |

The Copilot database has three schemas, one per layer:

| Schema | Layer | For reports? |
|---|---|---|
| `audit` | The records as loaded, the same records flattened into columns, the user directory, current licenses, inferred sessions, and the pipeline's own state. | Only for drill-through to a single record, and for checking the load. |
| `bi` | A star schema: narrow fact tables with integer keys, small dimensions, and a view over each. | **Yes. The `bi` views are the reporting interface.** |
| `dash` | What the Copilot Insights web dashboard keeps for itself: its settings, roles, org chart, daily rollups and the agent inventory. | Not by default. See section 12. |

In numbers, release 2.6.2 creates 104 tables and 37 views. A report needs 26 of them: the views in `bi`.

**What a record is.** One audit record is one Copilot interaction: a person's prompt and Copilot's response, in one app, at one moment. It says who, when, which app, which agent and model, what Copilot read to answer (files, mail, sites, web searches), and what the safety checks found. **It does not contain the prompt or the response text.** No table here has them.

*Reference: Part A, sections 1 and 2.*

## 2. How the data gets there, and when

A scheduled task on the job server runs once a night (`Invoke-CopilotPipeline.ps1`). In order:

1. The user directory, from on-premises Active Directory, into `audit.UserDirectory`.
2. Current Copilot licenses, from Microsoft Graph, into `audit.CopilotLicense`.
3. The agent workbook, when one is configured, into `dash.AgentInventory`.
4. License events, into the license-event database.
5. Copilot interactions, into `audit.CopilotAudit`. The same step then rebuilds, for the days it loaded, the flattened records, the sessions, the `bi` schema and the dashboard's rollups.
6. Where the administrators have set them up (2.6.2), usage and cost of GitHub Copilot and of Microsoft Foundry and Azure OpenAI deployments, into tables of their own in `audit`. They are optional, come last, and are not part of the `bi` views.

What that means for a report:

- **Data changes once a night.** Nothing arrives between runs. Refreshing a panel more often than hourly shows the same figures.
- **The last two days are not final.** Purview can deliver a record up to 48 hours after the event, so each night reads the last 48 hours again and adds what arrived late. A daily chart's last two points are low until then. The dashboard holds those two days back from its daily charts; do the same, or say so on the panel.
- **A night's refresh replaces its days in one transaction.** A query that runs during it sees the days either as they were or as they are, never half written.
- **A full rebuild is different.** After an upgrade or a repair, `bi.FactInteraction` is emptied and filled again a month at a time, which takes hours on a large history. Reports show partial history while it runs. The administrators schedule it and should tell you.
- **History can be restated.** A person's department and manager are today's, for all their history. An agent's name is its latest. Renaming an app's category changes which past interactions count as Premium. Section 5 has the list.

*Reference: Part A, sections 3 and 4.*

## 3. What to read: the `bi` views

Six fact views carry the numbers. Each joins to the dimension views by integer key.

```text
                         bi.Calendar (DateKey)
                               |
 bi.Users (UserKey) ----+------+------+---- bi.Apps (AppKey)
                        |             |
                  bi.Interactions     +---- bi.Agents (AgentKey)
                  one row per         +---- bi.Models (ModelKey)
                  interaction         +---- bi.LicenseStatuses (LicenseStatusKey)
                                      +---- bi.LicenseTypes (LicenseTypeKey)
                                      +---- bi.SensitivityLabels (SensitivityLabelKey)
                                      +---- bi.UseCases (UseCaseKey)
```

| Fact view | One row per | Joins to |
|---|---|---|
| `bi.Interactions` | Copilot interaction | Calendar, Users, Apps, Agents, Models, LicenseStatuses, LicenseTypes, SensitivityLabels, UseCases |
| `bi.Sessions` | Session: a person's prompts with less than 60 minutes between them, within one day | Calendar, Users, Apps, LicenseStatuses |
| `bi.AccessedResources` | Resource an interaction read: a file, a mail, a Teams message, a web search | Calendar, Users, Apps, ResourceTypes, ResourceActions, FileTypes, Sites, SensitivityLabels |
| `bi.LicensedDays` | Person per day on which they held a Copilot license at the end of the day | Calendar, Users |
| `bi.LicenseChanges` | Copilot SKU added to (`Change` = 1) or removed from (-1) a person | Calendar, Users, Skus |
| `bi.AgentEvents` | Copilot Studio authoring event: an agent created, published, shared, updated or deleted | Calendar, Users (by `ActorKey`) |

| Dimension view | Key | What it names |
|---|---|---|
| `bi.Calendar` | `DateKey` (yyyymmdd as an integer) | The date, month, quarter, weekday, and the Saturday-to-Friday week (`WeekEnding`, `WeekLabel`) |
| `bi.Users` | `UserKey` | Sign-in name, display name, department, division, job title, office, country, manager, the management chain (`OrgLevel1` to `OrgLevel15`), and today's license |
| `bi.Apps` | `AppKey` | The app (Word, Excel, Microsoft Teams, Copilot Chat ...) and its category: *Copilot Chat* or *In-App Copilot* |
| `bi.Agents` | `AgentKey` | The agent, its name and source (*Copilot Studio*, *Microsoft*, *Agent ID*). Key 1, *(no agent)*, is Copilot itself. From 2.6.3 an agent the audit log names two ways is one row |
| `bi.Models` | `ModelKey` | Model provider and name |
| `bi.LicenseStatuses` | `LicenseStatusKey` | 0 *Unknown*, 1 *Licensed*, 2 *Not licensed*: the license held at the time of use |
| `bi.LicenseTypes` | `LicenseTypeKey` | The license the record itself reports: *Premium*, *Starter*, *(not reported)* |
| `bi.Skus` | `SkuKey` | License SKU, and whether it is a Copilot SKU |
| `bi.SensitivityLabels` | `SensitivityLabelKey` | A sensitivity label's id. Names are in `bi.ValueNames` |
| `bi.ResourceTypes`, `bi.ResourceActions`, `bi.FileTypes`, `bi.Sites` | Their own keys | What a resource was, what was done to it, its file type, and the site it lives on |
| `bi.UseCases` | `UseCaseKey` | The use case an administrator's rules give each combination of app, web search and agent |
| `bi.JobFamilies` | `UserKey` | Each person's job family, from the administrator's rules on job titles |

Five more views stand alone:

| View | What it gives |
|---|---|
| `bi.LicensePool` | Seats bought, assigned and available, per SKU per day |
| `bi.DepartmentWeek` | Active people, interactions and sessions per department per week, with small groups hidden. Safe to show where individuals must not be |
| `bi.EventMarkers` | Events an administrator marked: a rollout wave, a training push, an outage. For chart annotations |
| `bi.ValueNames` | Readable names for the codes the audit log uses (label ids, model names, resource types) |
| `bi.OrgSecurity` | Who may see whom, from the org chart: one row per viewer and person. For row-level security |

*Reference: Part A, section 6, describes every column of every view. Part B has the types.*

## 4. Getting access

A reporting account reads the `bi` views and nothing else, through the database role `CopilotReporting`. The role is granted `SELECT` on each view in `bi`, not on the tables under them. That matters: the tables hold real names where the views hold pseudonyms in privacy mode (section 10).

Ask the database administrator for one of these.

**A Windows or service account.** The installer creates the login, the user and the role membership:

```powershell
.\Install-CopilotAudit.ps1 -SqlServer SQLPROD01 -ReportingAccount 'CORP\svc-grafana'
```

**A SQL login**, which Grafana on Linux usually needs. The role exists once the installer has been given `-ReportingAccount` at least once. Then, in the Copilot database:

```sql
CREATE LOGIN grafana_reader WITH PASSWORD = '<a strong password>';
CREATE USER grafana_reader FOR LOGIN grafana_reader;
ALTER ROLE CopilotReporting ADD MEMBER grafana_reader;
```

The account cannot read `audit`, `dash`, the `bi` tables or the license-event database. It does not need to. Two things it cannot find out for itself, which the administrator must tell you:

- **The reporting time zone**: the zone every day and hour in `bi` is cut in. `SELECT audit.ReportingTimeZone();` shows it to an administrator. It is a Windows zone name, such as `Eastern Standard Time`.
- **Whether privacy mode is on** (section 10).

With several tenants, each has its own pair of databases, and so its own Grafana data source.

*Reference: Part A, section 5.*

## 5. Rules every query follows

1. **Filter on `DateKey`.** It is an integer, `yyyymmdd`, in the reporting time zone. The fact tables are columnstore indexes in date order, so a range on `DateKey` reads only the days asked for.
2. **Days and hours are local, not UTC.** `DateKey`, `HourOfDay`, `ActivityHour`, `SessionStart` and `SessionEnd` are in the reporting time zone. Only `LicenseChanges.ChangedUtc` and `AgentEvents.EventUtc` are UTC. Section 8 shows how to give Grafana a correct time.
3. **Weeks run Saturday to Friday**, named by the Friday (`Calendar.WeekEnding`). A week that the period covers only in part counts fewer days, so its figures are lower.
4. **Every fact key is filled.** A missing user, app, agent or model points at a named row: *(unknown user)*, *(unknown)*, *(no agent)*, *(not reported)*. Inner joins lose nothing.
5. **`AgentKey` 1 is not an agent.** It is Copilot itself. An agent report filters `AgentKey > 1`.
6. **A person's attributes are today's.** `bi.Users` is rewritten every night from the directory. A person who moved department shows all their past use under the new one.
7. **`bi.Users` has people who never used Copilot**: everyone seen in the audit data, the licenses or the license history. Count active people from the facts, not from `bi.Users`.
8. **License status is the license held at the moment of use**, from the license history. It is fixed when the row is written. `Users.CopilotLicenseToday` is today's license, a different thing.
9. **`bi.Calendar` has every day of each year the data touches**, future days included. Start a query from a fact, or bound the calendar by the last day with data.
10. **Count people with `COUNT(DISTINCT UserKey)`.** Daily counts of people do not add up to a weekly count.

## 6. The main figures

These are the definitions the Copilot Insights dashboard and the Power BI model use. Build yours the same way and the three will agree.

| Figure | Definition |
|---|---|
| Interactions | Rows of `bi.Interactions` |
| Prompts | `SUM(PromptCount)` |
| Active users | `COUNT(DISTINCT UserKey)` over `bi.Interactions` in the period |
| Licensed users | People with a row in `bi.LicensedDays` on the last day of the period |
| Active licensed users | People who used Copilot while licensed in the period (`LicenseStatusKey = 1`) and still hold a license on its last day |
| Adoption % | Active licensed users divided by Licensed users. It cannot pass 100% |
| Licensed, not using | Licensed users with no interaction in the period |
| Premium interactions | Rows with `PremiumUse = 1`: licensed use that needed the license |
| Free interactions | Interactions less Premium interactions |
| Sessions | Rows of `bi.Sessions` |
| Prompts per session | `SUM(PromptCount)` over sessions, divided by Sessions |
| Multi-turn sessions | Sessions with `IsMultiTurn = 1` |
| Web-grounded interactions | Rows with `UsedWebSearch = 1` |
| Security signals | Rows with `JailbreakDetected`, `PromptInjectionDetected` or `HasBlockedResource` set |

**Premium and free use.** Every interaction made with a license either needed it or did not. *Premium*: Copilot inside Word, Excel, PowerPoint, Outlook or Teams; Copilot Chat in work mode; a Microsoft agent; or any chat that read work content. *Free*: web Copilot Chat, Copilot Studio agents, and anything done without a license. The nightly refresh decides it once per interaction and stores it in `PremiumUse`. Read the column; do not work the rule out again.

**The period's last day** is the last day with data, not today. Take it from the facts:

```sql
SELECT MAX(DateKey) AS LastDateKey FROM bi.Interactions;
```

*Reference: Part A, section 7, has each figure as SQL.*

## 7. Grafana: the data source

Add a **Microsoft SQL Server** data source for each Copilot database.

| Setting | Value |
|---|---|
| Host | The SQL Server and port, as the administrator gives them |
| Database | The Copilot database, `CopilotAudit` by default |
| Authentication | SQL Server Authentication with the login from section 4, or Windows authentication where your Grafana supports it |
| Encrypt | `true`. The installer's own connections are encrypted. Trust the server certificate only if the administrator says its certificate is not from a trusted authority |
| Min time interval | `1h`. Nothing is finer than an hour |
| Max open connections | A small number, such as 5. One dashboard opens a query per panel |

Then set, on each dashboard:

- **Time zone**: the reporting time zone, not *Browser* and not *UTC*. Days then start at midnight where the data's days do.
- **Week start**: Saturday.
- **Refresh**: off, or hourly at most.
- **A constant variable `tz`** holding the reporting time zone's Windows name, such as `Eastern Standard Time`. The queries below use it.

## 8. Grafana: time, variables and annotations

**The time range.** Grafana's range is UTC. The data's days are local. Each query turns the range into two `DateKey` values first:

```sql
DECLARE @from int = CONVERT(int, CONVERT(char(8), CAST(CAST($__timeFrom() AS datetimeoffset) AT TIME ZONE '$tz' AS date), 112)),
        @to   int = CONVERT(int, CONVERT(char(8), CAST(CAST($__timeTo()   AS datetimeoffset) AT TIME ZONE '$tz' AS date), 112));
```

**The time column.** A time series needs a column named `time` that is a true instant. A day's instant is its local midnight, turned into UTC:

```sql
CAST(CAST(c.[Date] AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time
```

For an hourly series, use `i.ActivityHour` in place of `CAST(c.[Date] AS datetime2)`.

Do not use `$__timeFilter()` or `$__timeGroup()` on `ActivityHour`. They treat it as UTC, which moves every interaction by the zone's offset and splits days in the wrong place.

**Variables.** A query variable returns `__text` and `__value`. Departments:

```sql
SELECT DISTINCT ISNULL(NULLIF(Department, N''), N'(no department)') AS __text,
       ISNULL(NULLIF(Department, N''), N'(no department)') AS __value
FROM bi.Users
ORDER BY 1;
```

Apps:

```sql
SELECT App AS __text, AppKey AS __value FROM bi.Apps ORDER BY App;
```

Use a multi-value variable in a query as `AND a.AppKey IN ($app)`. Give its *All* option the custom value `SELECT AppKey FROM bi.Apps`, so that *All* is not a list of every key.

**Annotations.** The events an administrator marked on the dashboard's trends make an annotation query:

```sql
SELECT CAST(CAST(Day AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       Label AS text, Kind AS tags
FROM bi.EventMarkers
WHERE Day BETWEEN CAST(CAST($__timeFrom() AS datetimeoffset) AT TIME ZONE '$tz' AS date)
              AND CAST(CAST($__timeTo()   AS datetimeoffset) AT TIME ZONE '$tz' AS date);
```

## 9. Queries to start from

Each begins with the two-line `DECLARE` from section 8, left out here after the first.

**Interactions and active users a day** (time series):

```sql
DECLARE @from int = CONVERT(int, CONVERT(char(8), CAST(CAST($__timeFrom() AS datetimeoffset) AT TIME ZONE '$tz' AS date), 112)),
        @to   int = CONVERT(int, CONVERT(char(8), CAST(CAST($__timeTo()   AS datetimeoffset) AT TIME ZONE '$tz' AS date), 112));
SELECT CAST(CAST(c.[Date] AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       COUNT(*) AS Interactions,
       COUNT(DISTINCT i.UserKey) AS [Active users]
FROM bi.Interactions AS i
JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY c.[Date]
ORDER BY c.[Date];
```

**Active users a week, whole weeks only** (time series):

```sql
SELECT CAST(CAST(c.WeekEnding AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       COUNT(DISTINCT i.UserKey) AS [Active users]
FROM bi.Interactions AS i
JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY c.WeekEnding
HAVING COUNT(DISTINCT i.DateKey) = 7
ORDER BY c.WeekEnding;
```

`HAVING COUNT(DISTINCT i.DateKey) = 7` drops a week with a day missing: the first, when the range starts mid-week, and the one under way. It also drops a week with a day on which nobody used Copilot, which a small tenant can have; the reference has a form that does not.

**Interactions by app** (time series, one line per app):

```sql
SELECT CAST(CAST(c.[Date] AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       a.App AS metric, COUNT(*) AS Interactions
FROM bi.Interactions AS i
JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
JOIN bi.Apps AS a ON a.AppKey = i.AppKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY c.[Date], a.App
ORDER BY c.[Date];
```

**Adoption** (stat panels): licensed users, those active, and the rate, on the last day with data in the range.

```sql
DECLARE @last int = (SELECT MAX(DateKey) FROM bi.LicensedDays WHERE DateKey BETWEEN @from AND @to);
WITH licensed AS (SELECT UserKey FROM bi.LicensedDays WHERE DateKey = @last),
     active AS (SELECT DISTINCT UserKey FROM bi.Interactions
                WHERE DateKey BETWEEN @from AND @last AND LicenseStatusKey = 1)
SELECT COUNT(*) AS [Licensed users],
       COUNT(a.UserKey) AS [Active licensed users],
       CAST(100.0 * COUNT(a.UserKey) / NULLIF(COUNT(*), 0) AS decimal(5, 1)) AS [Adoption %],
       COUNT(*) - COUNT(a.UserKey) AS [Licensed, not using]
FROM licensed AS l
LEFT JOIN active AS a ON a.UserKey = l.UserKey;
```

**Use by department** (table):

```sql
SELECT ISNULL(NULLIF(u.Department, N''), N'(no department)') AS Department,
       COUNT(DISTINCT i.UserKey) AS [Active users],
       COUNT(*) AS Interactions,
       SUM(CAST(i.PremiumUse AS int)) AS [Premium interactions]
FROM bi.Interactions AS i
JOIN bi.Users AS u ON u.UserKey = i.UserKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY ISNULL(NULLIF(u.Department, N''), N'(no department)')
HAVING COUNT(DISTINCT i.UserKey) >= 5
ORDER BY Interactions DESC;
```

The `HAVING` line leaves out departments with fewer than five active people (section 10).

**The agents most used** (table):

```sql
SELECT TOP (20) g.AgentName AS Agent, g.AgentSource AS Source,
       COUNT(DISTINCT i.UserKey) AS People, COUNT(*) AS Interactions
FROM bi.Interactions AS i
JOIN bi.Agents AS g ON g.AgentKey = i.AgentKey
WHERE i.DateKey BETWEEN @from AND @to AND i.AgentKey > 1
GROUP BY g.AgentName, g.AgentSource
ORDER BY Interactions DESC;
```

**Security signals a day** (time series):

```sql
SELECT CAST(CAST(c.[Date] AS datetime2) AT TIME ZONE '$tz' AT TIME ZONE 'UTC' AS datetime) AS time,
       SUM(CAST(i.JailbreakDetected AS int)) AS [Jailbreak attempts],
       SUM(CAST(i.PromptInjectionDetected AS int)) AS [Prompt injections],
       SUM(CAST(i.HasBlockedResource AS int)) AS [Blocked resources]
FROM bi.Interactions AS i
JOIN bi.Calendar AS c ON c.DateKey = i.DateKey
WHERE i.DateKey BETWEEN @from AND @to
GROUP BY c.[Date]
ORDER BY c.[Date];
```

**Seats** (stat panels), from the latest snapshot:

```sql
SELECT SkuPartNumber AS SKU, Bought, Assigned, Available
FROM bi.LicensePool
WHERE IsCopilot = 1 AND SnapshotDate = (SELECT MAX(SnapshotDate) FROM bi.LicensePool);
```

**How fresh the data is** (stat panel, and a good alert): days since the last day with data.

```sql
SELECT DATEDIFF(DAY, MAX(c.[Date]), CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE '$tz' AS date)) AS [Days behind]
FROM bi.Calendar AS c
WHERE c.DateKey = (SELECT MAX(DateKey) FROM bi.Interactions);
```

After a good night it is 0 or 1. Two or more means the nightly run failed or has not run.

*Reference: Part A, section 8, has about thirty more: sessions, resources, file types, sites, models, license changes, hours of the day, makers of agents, and a viewer's own people only.*

## 10. Privacy and who sees what

The `bi` views are person-level data. Whoever can open a Grafana dashboard built on them can see everything its queries return. Three things follow.

**There is no row-level security in SQL.** The Copilot Insights dashboard shows each manager only their own people, and the Power BI model does the same with a role. A Grafana data source has one login, and every viewer reads through it. If viewers must see only their own part of the organization, either build separate dashboards with the part fixed in the query, or use `bi.OrgSecurity` with a Grafana variable that holds the viewer's sign-in name (`${__user.login}`): it has one row per viewer and person they may see. The reference shows the join. Grafana does not stop a dashboard editor changing the query, so this protects viewers' dashboards, not editors'.

**Small groups.** The dashboard never shows a figure for fewer than five people (its *minimum group size*, which an administrator can raise). The `bi` views do not apply it, except `bi.DepartmentWeek`. Apply it yourself: `HAVING COUNT(DISTINCT UserKey) >= 5` on any breakdown by department, team or site.

**Privacy mode.** An organization that must not report on named people (under a works council agreement, for example) turns it on in the dashboard. The `bi` views then change by themselves:

- `bi.Users` gives a pseudonym (`P-` and sixteen characters) for every sign-in name, display name, manager and org level. It is the same for the same person everywhere, so counts and grouping still work. Department, job title and location stay as they are.
- `bi.OrgSecurity` is empty.
- `bi.Sites` names every OneDrive site *OneDrive (personal)*, and `bi.LicenseChanges.ChangedBy` is empty.

In privacy mode, prefer `bi.DepartmentWeek`, which never shows a person or a small group. Ask whether privacy mode is on before you design a report that lists people.

*Reference: Part A, sections 5 and 9.*

## 11. Keeping reports fast

The fact tables are clustered columnstore indexes. A year for a large organization is tens of millions of rows, and a query stays fast when it follows these:

- Always give a `DateKey` range.
- Aggregate in SQL. Return a row per point drawn, never raw rows.
- Join to a dimension after filtering, and group by its key or its name.
- `COUNT(DISTINCT UserKey)` over a year for every department at once is the costly one. Keep it to the periods and breakdowns a panel shows.
- Use Grafana's query caching where your edition has it, with a lifetime of an hour or more: the data changes once a night.
- Do not let a dashboard auto-refresh every few seconds. It only repeats the same queries.
- Avoid the hours of the nightly run for scheduled reports and alerts. Ask the administrator when it runs.

## 12. Where to look next

| For | Read |
|---|---|
| Every column of every `bi` view, with its meaning | [REPORTING-REFERENCE.md](REPORTING-REFERENCE.md), Part A, section 6 |
| Each figure as SQL, and how to check yours against the dashboard | Part A, sections 7 and 10 |
| More Grafana queries | Part A, section 8 |
| The other layers (`audit`, `dash`), and when a report needs them | Part A, section 9 |
| Every table and view with types, keys and indexes | Part B |
| The scripts that create it all, in full | Part C |
| What the web dashboard shows, to match its figures | [DASHBOARD.md](DASHBOARD.md) |
| How the pipeline is installed and run | [DEPLOYMENT.md](DEPLOYMENT.md), [OPERATIONS.md](OPERATIONS.md) |

**Beyond the `bi` views.** Three things a report sometimes needs are outside them, and need a grant the administrator makes by hand:

- **The agent inventory** (`dash.AgentInventory`, `dash.Agent`): each agent's owner, environment and settings from the agent workbook. `bi.Agents` has only what the audit log says.
- **The full record** (`audit.RawLogFlattened`): every field of one interaction, for drill-through from a figure to the records behind it.
- **AI use outside Microsoft 365 Copilot** (2.6.2): GitHub Copilot's seats, daily use, each person's day and cost (`audit.GitHubCopilotSeat`, `GitHubCopilotDay`, `GitHubCopilotUserDay`, `GitHubCopilotCost`), and the deployments, daily requests and tokens, and daily cost of Foundry and Azure OpenAI (`audit.FoundryDeployment`, `FoundryUsageDay`, `FoundryCostDay`). They are filled only where the administrators set up each loader. Days are UTC days, not the reporting time zone's; a person is a GitHub login, which nothing joins to `bi.Users` yet; Foundry has no person at all. Part A, section 9, lists them, and Part B has their columns.

The first two hold real names whatever privacy mode says, and the GitHub tables hold GitHub logins, so the administrator may refuse.
