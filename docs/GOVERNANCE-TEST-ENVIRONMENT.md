# Governance Workbench: test environment

A working Governance Workbench on one Windows PC, inside the Copilot Insights dashboard, connected to a real
Microsoft 365 tenant, with a demo institution and requests in every state. About 30 minutes the first time.
Every command runs from the root of the repository.

The Workbench has no web site of its own: the dashboard serves its pages under `/workbench/...` once the
dashboard's settings name the Workbench's database. Sign-in is the dashboard's (Windows here), and page access
comes from the dashboard's grants. [OPERATIONS.md](OPERATIONS.md#governance-workbench) is the reference for
both.

## What you need

- Windows 11, PowerShell 7, the .NET 10 SDK, a local SQL Server 2022 or later that accepts Windows sign-in and
  SQL logins, `sqlcmd`, and Microsoft Edge.
- PowerShell modules: `Microsoft.Graph.Authentication`, `ExchangeOnlineManagement`.
- An account that is Global Administrator (or Application, Exchange and Identity Governance administrator)
  in a test tenant with Entra ID P2 or E5, Power Platform with Copilot Studio, and Exchange Online.
- No admin rights on the PC: nothing here installs IIS, a Windows service or a machine certificate.

## 1. Set up the tenant

```powershell
pwsh -NoProfile -File governance\setup\Setup-TestTenant.ps1 -TenantDomain <your domain> -AdminUpn <your admin UPN> -WebBaseUrl http://localhost:5291
```

`-WebBaseUrl` is the dashboard's local address; the Teams app's links and the web app's redirect address use
it. Every sign-in opens in Microsoft Edge. Conditional Access may block device-code sign-in, so the script
never uses it. The script is safe to rerun: it finds what it made by display name. `-Steps` runs only some
parts, for example `-Steps Teams` or `-Steps Mailbox,PowerPlatform`.

What it creates:

| Item | Detail |
|---|---|
| Two certificates | `CN=CopilotGovernanceWeb-Test` and `CN=CopilotGovernanceWorker-Test` in your user certificate store, private keys not exportable. No client secrets. |
| Web app registration | Sign-in, five app roles and delegated Power Platform scopes. The dashboard does not read its app roles: page access comes from the dashboard's grants. It is used only if you turn on Entra sign-in for the dashboard with it ([ENTRA-SIGN-IN.md](ENTRA-SIGN-IN.md)). |
| Worker app registration | Application permissions `User.Read.All`, `GroupMember.Read.All`, `AccessReview.Read.All`, and for Copilot seats `LicenseAssignment.Read.All` and `Reports.Read.All`. No `Mail.Send` in Entra. |
| Test groups | Ten central role groups and five policy scope groups (`CGW Test - ...`). |
| Access packages | One catalog and one package per LOB tier, approval by a named approver. The worker is catalog reader on that catalog only. |
| Admin consent | The script opens each app's API permissions page in Edge; you click **Grant admin consent**. |
| Notification mailbox | A shared mailbox, with the worker's Mail.Send scoped to that mailbox only (Exchange RBAC for Applications). |
| Power Platform role | The worker gets Power Platform reader (a preview API). |
| Teams app | Uploaded to your org catalog and installed for you, with permission to send you activity-feed notices. |
| Copilot seats (`-Steps Seats`) | Adds `LicenseAssignment.Read.All` and `Reports.Read.All` to the worker and opens its consent page. Creates cloud stand-ins for the on-premises seat groups: `CGW Test - GSGu_MCO_CopilotM365` (you are a member; no license is assigned to it) and one `CGW Test - GSGu_MCO_<LOB>Tracking` group per test LOB. Record them on Workbench settings › Seat groups so the seat read uses them. |

The script writes the IDs it made to `governance\config\tenant.test.json`: tenant, the two app IDs and service
principal IDs, certificate subjects and thumbprints, group IDs, access package IDs, the catalog, the mailbox,
the Teams app and the seat stand-in groups. No secrets. The file is git-ignored.

## 2. Create the dashboard's demo database

```powershell
.\dashboard\demo\New-InsightsDemo.ps1 -SqlServer localhost -Database CopilotInsightsDemo -TrustServerCertificate -Force -Admins 'CONTOSO\me'
```

Use your own Windows account for `CONTOSO\me`: it becomes an Insights administrator in the demo.
`dashboard\CopilotInsights\appsettings.Development.json` already points the dashboard at `CopilotInsightsDemo`.
The demo also gives the worker Copilot activity to read for the seat read.

## 3. Create the Workbench's database

```powershell
pwsh -NoProfile -File governance\src\Governance.Database\Deploy-Database.ps1 -SqlServer localhost -Database CopilotGovernanceTest -CreateSqlLogins -LocalSettingsPath governance\config\appsettings.Local.json -TrustServerCertificate
```

This runs every script in `governance\src\Governance.Database\Scripts` in order. It makes two SQL logins with
random passwords, one for the dashboard (role `app_web`) and one for the worker (role `app_worker`), and writes
their connection strings to `governance\config\appsettings.Local.json` (git-ignored), as `Governance` and
`GovernanceWorker`. Neither login is a database owner. `-TrustServerCertificate` accepts the local SQL Server's
self-signed certificate; leave it off anywhere else. In production the dashboard and the worker connect with
their own service accounts instead.

## 4. Seed the demo institution

```powershell
dotnet run --project governance/tools/Governance.Seed -- --database CopilotGovernanceTest --sample-requests --tenant-settings governance\config\tenant.test.json
```

Pass `--tenant-settings`: without it the seed looks in a folder of the Workbench's old repository and maps no
access packages or groups. `--server` names another SQL Server (localhost by default). It creates:

- 46 fictional people, one per role, with `@example.invalid` mail. Each LOB has a business owner, two
  delegated approvers, a finance partner, a sponsor, a product owner and two makers. Central roles are the
  Platform Owner, Steering, intake, two operators, an architect, a risk reviewer, a FinOps lead and analyst,
  a commercial approver, a sourcing agent, a licensing owner, an auditor and an administrator.
- Your tenant account, from the tenant settings, with the Workbench roles of administrator and auditor only.
  Administrators cannot decide, so you act as the fictional people through the test-only **Act as** switch.
  Signed in with Windows you may be recorded as a second person; that changes nothing here, since your pages
  come from your dashboard grants and the work is done as the personas.
- Four funding owners, each passing the seven-item activation checklist: Retail Banking, Wealth
  Management, Commercial Banking, and Enterprise Shared IT (a critical shared service).
- Seven services, each with an approved control template (approved by two people) and verification checks.
  Also approved patterns, envelopes for the calendar year, two delegations per LOB, commercial inventory
  (a P3 plan renewing in about 75 days), and three registered environments with an approved posture.
- Canadian federal statutory holidays for this year and next. The demo institution is in Toronto, Eastern
  time.
- RACI version 1, approved by the Platform Owner and Steering and activated. Intake is opened.
- With `--sample-requests`, eight requests in different states, two consumption events, two findings and an
  open recertification campaign:

| Request | Where it stops |
|---|---|
| Branch product-terms assistant (J2) | Completed; workload in Development |
| Advisor meeting-prep agent (J2, production) | Production acceptance (D5), waiting for FinOps and Operations |
| Loan document checker (J2) | Returned to the requester (B7) |
| Branch scheduling connector (J3) | Approved with conditions; in the exception register |
| Mortgage status agent for customers (J3) | On hold: the requester must supply a privacy assessment |
| Raise agent limit (J4) | Overage authorization by the Retail business owner (B9) |
| Retire Wealth advisory - test (J7) | Retirement decision (I4) |
| Renew the P3 plan (J8) | Commercial Authority decision (F4) |

Running the seed again on a seeded database changes nothing.

## 5. Grant yourself the Workbench roles

The seed records your Workbench roles; which pages you may open comes from your dashboard grants. You are
already Admin (step 2), which opens the queues, the registry and every Workbench settings page. Add the
operator and auditor roles to reach Sync and freshness, Attestations and the reports. Either sign in later and
use Admin › Access › *Grant a role*, or now:

```powershell
sqlcmd -S localhost -d CopilotInsightsDemo -E -C -Q "INSERT dash.RoleGrant (Principal, PrincipalType, Role, Note, GrantedBy) SELECT N'CONTOSO\me', 'User', v.r, N'Workbench test', N'setup' FROM (VALUES ('GovernanceOperator'), ('GovernanceAuditor')) AS v(r) WHERE NOT EXISTS (SELECT 1 FROM dash.RoleGrant AS g WHERE g.Principal = N'CONTOSO\me' AND g.Role = v.r);"
```

Use the same account name as in step 2. The roles are `Governance`, `GovernanceOperator` and
`GovernanceAuditor`; [OPERATIONS.md](OPERATIONS.md#who-opens-which-pages) says what each opens.

## 6. Run it

Use three terminals, in this order.

The mock ITSM, on `http://localhost:5470`:

```powershell
dotnet run --project governance\tools\Governance.MockItsm
```

The worker. It reads its connection from `governance\config\appsettings.Local.json` and settings from
`CGW_` environment variables; give it the dashboard's address for links and the demo's activity for the seat
read:

```powershell
$env:CGW_Notifications__WorkbenchBaseUrl = 'http://localhost:5291'
$env:CGW_ConnectionStrings__Insights = 'Server=localhost;Database=CopilotInsightsDemo;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
dotnet run --project governance\src\Governance.Worker
```

The dashboard, with the Workbench on. `ConnectionStrings__Governance` turns it on; without it every
`/workbench` address is not found:

```powershell
$env:ConnectionStrings__Governance = (Get-Content governance\config\appsettings.Local.json -Raw | ConvertFrom-Json).ConnectionStrings.Governance
$env:Workbench__TenantSettingsPath = (Resolve-Path governance\config\tenant.test.json).Path
$env:Testing__AllowActAs = 'true'
dotnet run --project dashboard\CopilotInsights --launch-profile https
```

Open `http://localhost:5291/workbench/Start` in Edge. Windows signs you in.

`Testing:AllowActAs` works only in the Development environment (the launch profile sets it); the dashboard
refuses to start with it anywhere else. Leave it off in every other setting file.

## 7. Try one request end to end

**Act as a test persona** is offered at the top of each Workbench page to an Insights administrator, and is at
`/workbench/ActAs`. Every action records you as the real person behind it.

1. Act as Ethan Walsh (Retail Banking developer). On **Start**, choose **Build or register an agent**, answer
   the questions and submit. Keep the estimate under 2,000 credits a month, the audience to your team, data
   internal and the stage Development, so it fits the maker pattern.
2. Act as Aisha Rahman (intake): on **My work**, check the request and mark it complete.
3. Act as Martin Svensson (architect): classify it.
4. If it fits the maker pattern, act as Marcus Lee (Retail delegated approver) and approve, with a review date.
5. Act as Aisha Rahman again and prepare fulfillment. The ITSM task appears in the mock ITSM.
6. Close the ticket in the mock ITSM as Kevin Brooks (operator). The worker moves the request to verification.
7. Act as Yuki Tanaka (second operator) and verify. The request completes and the workload is in Development.

With `Notifications:Mode` left at `Record`, notices are written to the delivery log and nothing is sent. Set
`$env:CGW_Notifications__Mode = 'Send'` for the worker to send each step's email from the notification
mailbox and a Teams activity notice, once Exchange has applied the mailbox scope.

## Known limits

- The site runs on `http://localhost`. Production is HTTPS only.
- The Teams personal tab cannot load: Teams needs HTTPS. Activity notices still arrive and link to the site.
- The Power Platform inventory read on Sync and freshness needs the operator's own Entra sign-in. With Windows
  sign-in the page says so and reads nothing; the worker's daily reads are unaffected.
- Exchange can take 30 minutes to 2 hours to apply the mailbox scope. Until then email delivery fails and is
  retried; the delivery log shows it.
- The Power Automate flow that posts cards to Teams channels is not created yet. Channel notices are
  skipped and email stays the notice of record.
- The dashboard and the worker run as console processes, not under IIS and a Windows service, so several web
  servers, the shared key ring and service accounts are not exercised here.
