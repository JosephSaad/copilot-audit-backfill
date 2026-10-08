# GitHub Copilot usage: setup

How to load the usage and cost of **GitHub Copilot Enterprise** (or Business) into the Copilot database, with `tools\Import-GitHubCopilotUsage.ps1`. This is the first plumbing (2.6.2): the tables, the load and its log. No dashboard report reads the tables yet; a reporting tool can.

Two people are involved. A **GitHub enterprise owner** does part 2, which needs no access to the job server or to SQL Server: hand them that part as it is. The **job server's administrator** does the rest.

The loader has been run end to end against a stand-in for GitHub's API that answers as GitHub documents it. It has **not** been run against GitHub itself: check the first real load against GitHub's own pages (part 5).

## 1. The values needed

Collect these before starting. The last column says who has each.

| Value | Example | Where it goes | Who has it |
|---|---|---|---|
| Enterprise slug | `contoso` | `GitHubEnterprise` in `copilot-audit.settings.psd1` | The enterprise owner. It is in the address of the enterprise's pages: `https://github.com/enterprises/contoso`. It is not the display name. |
| A personal access token (classic) | `ghp_...` | Typed once on the job server (`-SaveToken`); never in the settings file | The enterprise owner creates it (part 2). |
| The token's expiry date | 2027-01-31 | Nowhere: Admin › Health shows it after the first load | The enterprise owner chooses it. |
| API host, only for GitHub Enterprise Cloud with data residency | `https://api.contoso.ghe.com` | `GitHubApiBase` in the settings file | The enterprise owner. Leave it out for github.com. |
| Where the token file is kept, only if not beside the settings file | `D:\Secrets\github-copilot.token` | `GitHubTokenPath` in the settings file | The job server's administrator. |

To load one organization instead of the whole enterprise, the value is the organization's name, in `GitHubOrganization` in place of `GitHubEnterprise`. Give one or the other, not both.

Nothing else is needed. SQL Server and the database come from the settings the nightly run already has.

## 2. For the GitHub enterprise owner

*This part can be sent as it is.*

We load GitHub Copilot's usage figures and its cost into our own reporting database once a night, read-only. We need three things from an **enterprise owner** (a billing manager can do steps 2 and 3, not step 1).

**1. Turn on usage metrics.** In the enterprise: **AI controls** (or **Policies**) › **Copilot** › **Metrics**, set *Copilot usage metrics* to **Enabled everywhere** (or enabled for the organizations to be reported on). Without it, GitHub gives no reports. Reports begin from the day it is on.

**2. Create a token.** Signed in as the enterprise owner: **Settings** (your own) › **Developer settings** › **Personal access tokens** › **Tokens (classic)** › **Generate new token (classic)**.

| Field | Set it to |
|---|---|
| Note | `Copilot usage reporting (read-only)` |
| Expiration | A date you will renew by. One year is usual; we are warned 14 days before it. |
| Scopes | `manage_billing:copilot` and `read:enterprise`. Nothing else. |

Use a classic token: GitHub's enterprise endpoints for Copilot seats and billing take those. If the enterprise's organizations enforce SAML single sign-on, choose **Configure SSO** next to the token afterwards and authorize it for them.

What the token can do: read the enterprise's Copilot seats, its Copilot usage reports and its billing usage. It cannot read code, change a seat or change a setting.

**3. Send us**, by a channel fit for a secret: the token; the enterprise's slug (the last part of `https://github.com/enterprises/<slug>`); the token's expiry date; and, if the enterprise is on GitHub Enterprise Cloud with data residency, its API host (`https://api.<name>.ghe.com`).

**What is read, and how often.** Once a night: the list of Copilot seats (login, organization, team, plan, last activity), the day's usage report for the enterprise and for each person who used Copilot (counts, lines of code, AI credits; no prompts and no code), and the billing usage lines for Copilot for this month and the last.

**When the token nears its expiry**, create a new one the same way and send it. To withdraw access at any time, delete the token.

## 3. On the job server

As an administrator, in PowerShell 7, from the folder the scripts are in (the one with `Invoke-CopilotPipeline.ps1`).

**1. The tables.** They come with the release: `Install-CopilotAudit.ps1` creates them. If the database was upgraded to 2.6.2, they are there.

**2. Save the token.** It asks for the token and does not show it:

```powershell
.\tools\Import-GitHubCopilotUsage.ps1 -SaveToken
```

It writes `github-copilot.token` beside `copilot-audit.settings.psd1`, encrypted so that only this computer can read it back (Windows data protection, machine scope). Any account on this computer could, so let only administrators and the nightly task's account read the file:

```powershell
icacls .\github-copilot.token /inheritance:r /grant:r "BUILTIN\Administrators:F" "CONTOSO\svc-copilot$:R"
```

Use the account the task *Copilot audit - nightly* runs as. A token saved on one job server cannot be copied to another: save it again there.

**3. Load the history once.** GitHub keeps up to a year of reports; 90 days is a reasonable start:

```powershell
.\tools\Import-GitHubCopilotUsage.ps1 -Enterprise contoso -SqlServer SQLPROD01 -Days 90
```

Add `-Database` if it is not `CopilotAudit`, and `-TrustServerCertificate` if the nightly run uses it. It takes about two requests a day of history, so some minutes for 90 days. Its last line says what was loaded:

```
Succeeded. Loaded 412 seat(s), 88 day(s) of usage, 21530 person-day(s), 61 cost line(s).
```

**4. Turn on the nightly load.** Add to `copilot-audit.settings.psd1`:

```powershell
    GitHubEnterprise = 'contoso'
    # GitHubApiBase  = 'https://api.contoso.ghe.com'      # data residency only
    # GitHubTokenPath = 'D:\Secrets\github-copilot.token'  # only if the token file is elsewhere
```

From the next night, the run has a last step, *GitHub Copilot usage*, which loads the last 10 days again (GitHub's reports arrive up to three days late, and a day's figures can still change). Without `GitHubEnterprise` or `GitHubOrganization` in the settings, the run skips the step and says so in its log; nothing fails.

**The step is optional to the run.** If it fails (an expired token, GitHub unreachable), the failure is on its own step row and in `audit.ExternalUsageRun`, and the rest of the night, already done by then, stands as succeeded.

**Network.** The job server needs outbound HTTPS to `api.github.com` (or the data-residency API host), and to the hosts the report download links point to, which GitHub chooses. Those hosts were not verified here: if the seats load and the usage does not, with a connection error, look at the address in the error and allow it.

## 4. What it fills

| Table | One row per | Kept how |
|---|---|---|
| `audit.GitHubCopilotSeat` | Seat: login, organization, team, plan, assigned, last used | Replaced whole each run: it is the seats as they are now |
| `audit.GitHubCopilotDay` | UTC day, for the enterprise: active people, interactions, code generated and accepted, lines of code, and the whole record as JSON | The days loaded are written again; older days stay |
| `audit.GitHubCopilotUserDay` | Person per UTC day they used it: interactions, code, AI credits used, chat, agent, command line | The same |
| `audit.GitHubCopilotCost` | Billing line for Copilot: day, product, SKU, model, organization, quantity, price, gross, discount and net (US dollars) | This month and the last, replaced whole |
| `audit.ExternalUsageRun` | Run: when, the days covered, status, rows, what was loaded or why not, the token's expiry | Every run kept |

[SCHEMA.md](SCHEMA.md#ai-usage-outside-microsoft-365-copilot-262) has every column.

**What it does not give.** Prompts, responses and code are not in GitHub's reports. A person is a GitHub login, which is not a Microsoft 365 sign-in: nothing here joins a login to the directory yet, so these figures cannot be cut by department. Days are UTC days, not the reporting time zone's.

## 5. Checking it

**Admin › Health**, panel *Other AI usage: GitHub Copilot and Foundry*: when it last loaded, the days and rows, the newest day held, and when the token expires. It turns amber for a partial load, a token within 14 days of expiry, or no load for two days; red when the last run failed, with GitHub's own message.

**In SQL:**

```sql
-- The last runs
SELECT TOP (5) StartedUtc, Status, FromDay, ToDay, RowsWritten, Detail, CredentialExpiresUtc
FROM audit.ExternalUsageRun WHERE Source = 'GitHub' ORDER BY RunId DESC;

-- Seats by plan, and how many were used in the last 30 days
SELECT PlanType, COUNT(*) AS Seats,
       SUM(CASE WHEN LastActivityUtc >= DATEADD(DAY, -30, SYSUTCDATETIME()) THEN 1 ELSE 0 END) AS UsedIn30Days
FROM audit.GitHubCopilotSeat GROUP BY PlanType;

-- Use by day
SELECT [Day], DailyActiveUsers, MonthlyActiveUsers, Interactions, CodeAcceptances, LocAdded
FROM audit.GitHubCopilotDay ORDER BY [Day] DESC;

-- What was charged, by month and SKU
SELECT DATEFROMPARTS(YEAR(UsageDate), MONTH(UsageDate), 1) AS [Month], Sku, SUM(Quantity) AS Quantity, SUM(NetAmount) AS NetUsd
FROM audit.GitHubCopilotCost GROUP BY DATEFROMPARTS(YEAR(UsageDate), MONTH(UsageDate), 1), Sku ORDER BY [Month] DESC, NetUsd DESC;

-- AI credits by person, last 30 days (1 credit is 0.01 US dollars at list price)
SELECT TOP (20) Login, SUM(AiCreditsUsed) AS Credits, SUM(AiCreditsUsed) * 0.01 AS ListUsd
FROM audit.GitHubCopilotUserDay WHERE [Day] >= DATEADD(DAY, -30, CAST(SYSUTCDATETIME() AS DATE))
GROUP BY Login ORDER BY Credits DESC;
```

**Against GitHub**, after the first real load: the seat count against the enterprise's **Billing and licensing** › **Licensing** page; a day's active users against the Copilot usage dashboard in **AI controls**; a month's net amount against the **Usage** page filtered to Copilot. Report a difference with the run's `Detail` and one day's `Json` from `audit.GitHubCopilotDay`.

## 6. When it does not work

| The run's detail or the error says | It means | Do |
|---|---|---|
| `No GitHub token` | No token file where the settings say, and no `GITHUB_COPILOT_TOKEN` | Step 2 of part 3; check `GitHubTokenPath` |
| `401: Bad credentials` | The token expired, was deleted, or was mistyped | A new token (part 2), then `-SaveToken` |
| `403` ... `SAML` or `SSO` | The token is not authorized for single sign-on | *Configure SSO* on the token |
| `403` or `404` on seats | The token lacks `manage_billing:copilot` or `read:enterprise`, its owner is not an enterprise owner or billing manager, or the slug is wrong | Check the scopes, the owner's role and the slug |
| Seats load, usage says every day had no report | The usage metrics policy is off, or was turned on today | Step 1 of part 2; reports begin the day after |
| `GitHub has no billing usage for it` | The token's owner cannot read billing, or the enterprise is not on GitHub's current billing platform | An enterprise owner's or billing manager's token |
| The key cannot be read: `Unprotect` fails | The token file was saved on another computer | `-SaveToken` on this one |
| `The tables are not in ... yet` | The database is from before 2.6.2 | `Install-CopilotAudit.ps1` from this release |

A run that ends **Partial** loaded what it could: its detail names the part it could not, and the next run tries all of it again.

## 7. Parameters

`tools\Import-GitHubCopilotUsage.ps1`, all optional when the settings file has them:

| Parameter | Default | Use |
|---|---|---|
| `-Enterprise` | `GitHubEnterprise` in the settings | The enterprise's slug |
| `-Organization` | `GitHubOrganization` in the settings | One organization instead |
| `-Days` | 10 | How many complete UTC days to load, ending yesterday; up to 366 |
| `-SqlServer`, `-Database`, `-TrustServerCertificate` | From the settings; database `CopilotAudit` | Where to write |
| `-ConnectionString` | None | In place of the three above |
| `-SaveToken` | | Asks for the token, saves it encrypted, and loads nothing |
| `-TokenPath` | `GitHubTokenPath`, else `github-copilot.token` beside the settings file | Where the token file is |
| `-Token` | | The token as a secure string, for a single run; the environment variable `GITHUB_COPILOT_TOKEN` does the same |
| `-ApiBase` | `https://api.github.com` | The API host, for data residency |
| `-ApiVersion` | `2026-03-10` | GitHub's API version |
| `-SettingsPath` | `copilot-audit.settings.psd1` in the scripts' folder | Another settings file |
