# The weekly Copilot report

An email every Tuesday morning with one table and two charts, and an Excel workbook attached, for the week that ended the Friday before. It runs on the job server with no one present. Part of the release from 3.1.2; for 3.1.1 it was an add-on with the same files (`copilot-weekly-report-3.1.1.zip`). On upgrade from the add-on, replace only `tools\Send-CopilotWeeklyReport.ps1` with 3.1.2's: the add-on's copy sends its emails without the charts in Windows PowerShell 5.1.

## What it sends

**The table**: eight figures for the week (Saturday 00:00 to Friday 23:59:59.9999), in two columns (3.1.3):

| | M365 Copilot | Copilot Chat |
|---|---|---|
| **Who** | Use made with a Microsoft 365 Copilot license at the time | Use made without one (and use the license history could not place) |
| **Number of Users Enabled** | The members of the license group (`CopilotLicenseGroup`): enabled accounts only, or every member, as set on the dashboard | The members of the Chat group (`CopilotChatGroup`), the same way |
| **Active Users** | People who used Copilot with a license in the week: with an account enabled today only, or everyone, as set on the dashboard | The same, without a license |
| **Number of actions taken** | Interactions made with a license: one for each Copilot audit record, a prompt and its answer, whoever made it. Not sessions, not people | The same, without a license |
| **Security Alerts** | Interactions made with a license that had a jailbreak attempt, a prompt injection in content Copilot read, or a blocked resource (the dashboard's *Security signals*), whoever made them | The same, without a license |

**Disabled accounts and people who have left are counted differently in each row** (3.1.3), and the report says so in a box under the table, in the email, its text version and the workbook. By default:

| Row | Disabled accounts and leavers |
|---|---|
| Number of Users Enabled | Not counted |
| Active Users | Not counted: it counts people, and only accounts enabled today |
| Number of actions taken | Counted: every interaction of the week stays, so it does not drop when someone leaves |
| Security Alerts | Counted, the same way |

So actions taken can include use by people who are not in Active Users. Set to count everyone (below), the two people rows count them too, and the box says so.

**What each number means** is under the box: a table of the 8 values, each with its definition for each column and whether disabled accounts count, naming the two groups and the day they were counted. The workbook's *Summary* sheet has the same, above the charts.

**Counting everyone, disabled accounts included,** is one switch for both people rows: Admin › Alerts and digests › *Weekly Copilot report*, *Users Enabled and Active Users count*: *Accounts enabled today only* (the default) or *Everyone, disabled accounts and leavers included*. With everyone, Users Enabled counts every member of the groups and Active Users everyone who used Copilot in the week. The panel also shows each group's latest count: all members, enabled and disabled. The nightly load keeps both counts, so the setting changes every week of the next report, the trend's included. Actions taken and Security Alerts count every interaction either way. For one run only, `Send-CopilotWeeklyReport.ps1 -IncludeDisabled` counts everyone (with `-Preview` to look first), and `bi.usp_WeeklyCopilotReport @GroupCount = 'Enabled'` or `'All'` does the same in SQL; the setting is `WeeklyReportGroupCount` in `dash.Setting`.

**Users Enabled comes from two AD groups**, named on the dashboard (Admin › Directory quality › *Where users and groups are read from*, with the users' AD domain), or in `copilot-audit.settings.psd1` (never in code); the dashboard's wins where both are set:

```powershell
    CopilotLicenseGroup = 'CORP\Copilot-M365-License'
    CopilotChatGroup    = 'CORP\Copilot-Chat'
```

**To check a count against AD**, from any machine with the ActiveDirectory module, name the users' domain with `-Server` on every command: the job server or the dashboard can be in another domain, and without it the commands ask that one. The enabled members of the license group, nested groups included:

```powershell
$chkDomain = 'users.contoso.com'
$chkGroup  = 'Copilot-M365-License'
$chkDn     = (Get-ADGroup -Identity $chkGroup -Server $chkDomain).DistinguishedName
$chkFilter = "(memberOf:1.2.840.113556.1.4.1941:=$chkDn)"
$chkUsers  = Get-ADUser -LDAPFilter $chkFilter -Server $chkDomain -ResultSetSize $null
($chkUsers | Where-Object Enabled).Count
```

Every member, disabled included, is `$chkUsers.Count`. The filter finds members through nested groups, as the nightly load does; `Get-ADGroupMember -Recursive` stops at 5,000 members by default, which a Copilot Chat group passes. The report's count for a day is the same, taken by the nightly load, which reads AD over LDAP from the same domain.

Each night the AD load (`tools\Export-UsersWithHierarchy-ToSql.ps1`) reads both groups' members, nested groups included, and records each group's count and how many of those accounts are enabled (`audit.ReportGroupDay`). A week takes the count from the last night the load ran in it; the load early on a Saturday closes the week that ended on the Friday. **Weeks before the groups were first counted** (3.1.3): AD keeps no history of a group's members, so M365 Copilot's Users Enabled is estimated for those weeks from the people holding a Microsoft 365 Copilot license on the week's last day, which the license group gives (enabled accounts only, or every holder, as the setting says). The report marks it *ESTIMATED* in the definitions, says how many of the weeks shown are estimated, and the workbook's *Weekly* sheet says for each week whether the figure is from the AD group or estimated from licenses. Copilot Chat has no such history, so its Users Enabled stays blank for those weeks and the chart leaves a gap. A week after the first count with no count (a night the load did not run) is not estimated: it shows blank.

**The note under the table** names anyone counted in Active Users but not in Users Enabled: people who used Copilot with a license but are not in the license group today, or without a license and not in the Chat group. With licenses assigned through the group, it is the few whose license and group membership are out of step.

**The columns split by license, not by group.** Each interaction counts where the license stood that day, so the two columns add up to all use, past weeks do not change when someone moves group, and nobody is counted twice. Someone licensed part-way through a week can be in both columns' Active Users.

**Two charts**, one point per week for the 13 weeks ending with the reported one (about 90 days, `Weeks` in the settings): *Copilot Chat Enabled vs. Active vs. Actions Taken* and *M365 Copilot Users Enabled vs. Active vs. Actions Taken*. Users enabled and active users are lines on the left axis; actions taken is the shaded area on the right axis. Each week is labeled by the Saturday it starts.

**The workbook** (`Copilot weekly report <week ending>.xlsx`): *Summary* has the table, the box on disabled accounts, what each number means, the notes and both charts as native Excel charts that can be edited; *Weekly* has every week's figures, both columns and security alerts included, and where each week's M365 Copilot users enabled comes from; *Notes* says how each figure is counted.

**In their words** (3.1.6): up to three success stories, as people wrote them on the dashboard (menu: *Success stories*), approved and ticked for the report by a curator (the Stories role). Each names the person only where they agreed and privacy mode is off; otherwise "a colleague in" their department, or "a colleague" where it is smaller than the minimum group size. The workbook's *Stories* sheet lists every approved story to date, with when each was sent. A report sent marks its stories sent and clears their ticks, so each goes out once unless a curator ticks it again; a preview (`-Preview`) or a test (`-To`) quotes them and marks nothing. Without a ticked story, the email has no such part. `StoriesPerReport` (dashboard setting, 3) sets how many; 0 leaves them out.

**Days are in the reporting time zone** (`audit.ReportingTimeZone()`, the dashboard's), the same days `bi.Calendar` and every report use.

**The figures come from the Copilot database and AD**, not the Microsoft 365 admin center, so they differ from its reports, which count activity their own way.

## When it sends

The task runs every hour. Each run checks whether the last complete week is due: on or after Tuesday 08:00 in the reporting time zone (`SendDay`, `SendTime`) and not yet sent. When it is:

1. **It waits for the data.** Purview can deliver a record hours after the event, and the nightly run reloads the last 48 hours. The week is taken as complete once the data reaches the Monday after it. Until then the run waits, for up to `MaxWaitHours` (24) after the send time; after that it sends, with a note at the top saying the last days may be low.
2. **It sends once.** Each week sent is recorded in `audit.WeeklyReportSend`. A week already sent is not sent again.
3. **It tries again on failure.** A send the relay refuses is recorded as *Failed* and tried on the next hourly run, up to `MaxAttempts` (8) times.
4. **It catches up.** If the server was down on Tuesday, the first run after it comes back sends the week.
5. **It reminds the curators** (3.1.6). The day before the send day, from the send time, one email a week goes to `StoriesReminderTo` (dashboard setting; empty: none) when success stories wait for review or none is ticked for the report.

## What it needs

- **The job server**, with release 3.1.1 installed and its nightly task running.
- **Windows PowerShell 5.1** (`powershell.exe`, part of Windows) with .NET Framework 4.x. The charts in the email are drawn with the .NET Framework's chart library, which PowerShell 7 does not have. The rest of the release keeps running in PowerShell 7.
- **ImportExcel** in the release's `Modules` folder, as `tools\Save-CopilotAuditModules.ps1` puts it there (from 2.5.7). Without it the email goes without the workbook, and the log says so.
- **Email set up in the dashboard** (Admin › Email, see DASHBOARD.md) and working: *Send a test* there succeeds. The job server must reach that server too (the web server sent the test). From 3.1.3 the report sends through that same server, with its port, security, user name and password, certificate check, From and Reply-To, read from the Copilot database (`dash.Setting`, and the password through `dash.usp_GetSecret`). Nothing about the mail server is in the report's settings or stored on the job server, and a change in Admin › Email applies to the next report. Two of the dashboard's choices cannot be used by the report, which says so: TLS from the first byte (*Implicit*, port 465), and Exchange Online with OAuth. Choose *StartTls* (port 587 or 25), or Exchange Online with a password. With *Auto* on another port, the report requires STARTTLS.
- **The nightly task's account** (it already reads and writes the Copilot database). It needs, in the Copilot database, to execute `bi.usp_WeeklyCopilotReport` and to write `audit.WeeklyReportSend`; `db_owner`, which the installer gives it, covers both.

## Installing it

On the job server, as an administrator, from the folder the scripts are in (the one with `Invoke-CopilotPipeline.ps1`, for example `D:\CopilotAudit`).

**1. The files.** From 3.1.2 they come with the release, in the folders they run from: `sql\Create-WeeklyReport.sql`, `tools\Send-CopilotWeeklyReport.ps1`, `tools\Register-CopilotWeeklyReport.ps1` and `CopilotWeeklyReport.settings.example.psd1`. On 3.1.1, copy them in from `copilot-weekly-report-3.1.1.zip` (or split them out of `copilot-weekly-report-3.1.1-bundle.txt`), to the same folders. If Windows marks the files as downloaded, unblock them:

```powershell
Get-ChildItem -Recurse -Filter *Weekly* | Unblock-File
```

**2. The table and the procedure.** From 3.1.2 `Install-CopilotAudit.ps1` creates them, with every other object. On 3.1.1, or to create them alone, run the script as someone with `db_owner` in the Copilot database (the account that installed the release):

```powershell
sqlcmd -S SQLPROD01 -d CopilotAudit -E -b -i .\sql\Create-WeeklyReport.sql
```

Use your SQL Server name. It is safe to run again.

**3. Fill in the settings.**

```powershell
Copy-Item .\CopilotWeeklyReport.settings.example.psd1 .\CopilotWeeklyReport.settings.psd1
```

```powershell
notepad .\CopilotWeeklyReport.settings.psd1
```

Set `To` (and `Cc`, `Bcc` if wanted). The mail server and the From address come from Admin › Email, and the database from `copilot-audit.settings.psd1`, so `SqlServer` and `Database` can stay empty. From 3.1.3 the report no longer reads `SmtpServer`, `SmtpPort`, `SmtpUseSsl`, `SmtpUseDefaultCredentials` or `From`; a settings file from 3.1.2 that still has them works, and the log says they can be deleted.

**4. Preview it.** This sends nothing and records nothing:

```powershell
powershell.exe -NoProfile -File .\tools\Send-CopilotWeeklyReport.ps1 -Preview
```

Open the files it names, under `reports\preview`: the `.html` is the email as it will look, with the two `.png` charts beside it, and the `.xlsx` is the workbook. To preview another week, add `-WeekEnding 2026-10-09` (a Friday).

**5. Send a test to yourself.** This goes only to the address given and is not recorded, so Tuesday's send still goes:

```powershell
powershell.exe -NoProfile -File .\tools\Send-CopilotWeeklyReport.ps1 -To you@contoso.com
```

**6. Schedule it.** Elevated:

```powershell
powershell.exe -NoProfile -File .\tools\Register-CopilotWeeklyReport.ps1
```

The task, *Copilot audit - weekly report*, runs every hour as the nightly task's account. For another account, add `-ServiceAccount 'CORP\gmsa-copilot$'` (a gMSA) or `-ServiceAccountCredential (Get-Credential)`.

**7. Check it after the first Tuesday.**

```sql
SELECT WeekEnding, Status, Attempts, SentUtc, Recipients, DataComplete, Detail
FROM audit.WeeklyReportSend ORDER BY WeekEnding DESC;
```

The log is `logs\weekly-report.log`; each sent week's email, charts and workbook are kept under `reports\<week ending>` (`KeepCopies`).

## Running it by hand

| To | Run |
|---|---|
| Send last week now, if not yet sent | `powershell.exe -NoProfile -File .\tools\Send-CopilotWeeklyReport.ps1` |
| Send a week again | `... -WeekEnding 2026-10-09 -Force` |
| See a week without sending | `... -WeekEnding 2026-10-09 -Preview` |
| Send to one address only | `... -To you@contoso.com` |
| Stop the weekly email | `powershell.exe -NoProfile -File .\tools\Register-CopilotWeeklyReport.ps1 -Unregister` |

The figures alone, in SQL Server Management Studio:

```sql
EXEC bi.usp_WeeklyCopilotReport;                              -- the last complete week
EXEC bi.usp_WeeklyCopilotReport @WeekEnding = '2026-10-09';   -- a given week (a Friday)
```

It returns the week (dates, the first day of data, the last day with data and whether the data is complete), the table, and the weeks for the charts.

## When it does not work

| The log says | It means | Do |
|---|---|---|
| `No settings file at ...` | Step 3 was not done | Copy and fill in the settings |
| `Could not find stored procedure 'bi.usp_WeeklyCopilotReport'` | Step 2 was not done | Run `Install-CopilotAudit.ps1`, or `Create-WeeklyReport.sql` alone |
| `Charts not drawn ...` | The chart library could not load: the script ran in PowerShell 7, or .NET Framework 4.x is missing | Run it with `powershell.exe`; the task does |
| `Charts not drawn ... Cannot convert value "7/25" to type "System.Double"` | The 3.1.1 add-on's copy of the script | Replace `tools\Send-CopilotWeeklyReport.ps1` with 3.1.2's |
| `Workbook not written ...` | ImportExcel is not in `Modules` | `tools\Save-CopilotAuditModules.ps1` on a machine with gallery access, then copy `Modules` over |
| `Email is off in the dashboard` | Admin › Email is set to off | Set it up there; *Send a test* first |
| `has no dashboard settings` | The Copilot database was installed without `-Dashboard` | `Install-CopilotAudit.ps1 -Dashboard`, then Admin › Email |
| `changed after the password was stored` | The server, port, security, certificate check or user name in Admin › Email changed since the password was typed | Type the password again in Admin › Email |
| `TLS from the first byte` or `OAuth` | Admin › Email uses a setting the report cannot | *StartTls* (587 or 25), or Exchange Online with a password |
| `The remote certificate is invalid` | The server's certificate does not match its name or is not trusted on the job server | Trust its issuer on the job server, or tick *Accept any server certificate* in Admin › Email if the dashboard needs it too |
| `the email was not sent: ... 5.7.1 ...`, `535` or `Connection refused` | The server refused the job server, the user name or password, or the port is wrong | *Send a test* in Admin › Email; ask the mail team to allow the job server |
| `data reaches ..., not yet the Monday after the week. Waiting` | The nightly run has not loaded the days after the week | Nothing, unless it lasts: check the nightly run on Admin › Health |
| `-WeekEnding must be a Friday` | The date given is not a Friday | Give the Friday the week ends on |

## Uninstalling

```powershell
powershell.exe -NoProfile -File .\tools\Register-CopilotWeeklyReport.ps1 -Unregister
```

```sql
DROP PROCEDURE IF EXISTS bi.usp_WeeklyCopilotReport;
DROP TABLE IF EXISTS audit.WeeklyReportSend;
```
