# Test environment

A working Workbench on one Windows PC, connected to a real Microsoft 365 tenant, with a demo institution
and requests in every state. About 30 minutes the first time.

## What you need

- Windows 11, PowerShell 7, the .NET 10 SDK, a local SQL Server 2022 or later (2025 recommended) that
  accepts Windows sign-in and SQL logins, and Microsoft Edge.
- PowerShell modules: `Microsoft.Graph.Authentication`, `ExchangeOnlineManagement`.
- An account that is Global Administrator (or Application, Exchange and Identity Governance administrator)
  in a test tenant with Entra ID P2 or E5, Power Platform with Copilot Studio, and Exchange Online.
- No admin rights on the PC: nothing here installs IIS, a Windows service or a machine certificate.

## 1. Set up the tenant

```powershell
pwsh -NoProfile -File setup\Setup-TestTenant.ps1 -TenantDomain <your domain> -AdminUpn <your admin UPN>
```

Every sign-in opens in Microsoft Edge. Conditional Access may block device-code sign-in, so the script
never uses it. The script is safe to rerun: it finds what it made by display name. `-Steps` runs only some
parts, for example `-Steps Teams` or `-Steps Mailbox,PowerPlatform`.

What it creates:

| Item | Detail |
|---|---|
| Two certificates | `CN=CopilotGovernanceWeb-Test` and `CN=CopilotGovernanceWorker-Test` in your user certificate store, private keys not exportable. No client secrets. |
| Web app registration | Sign-in, five app roles (User, Central, Operator, Admin, Auditor), delegated Power Platform API scopes for the operator's inventory sync. You are assigned every app role. |
| Worker app registration | Application permissions `User.Read.All`, `GroupMember.Read.All`, `AccessReview.Read.All`, and for Copilot seats `LicenseAssignment.Read.All` and `Reports.Read.All`. No `Mail.Send` in Entra. |
| Test groups | Ten central role groups and five policy scope groups (`CGW Test - ...`). |
| Access packages | One catalog and one package per LOB tier, approval by a named approver. The Worker is catalog reader on that catalog only. |
| Admin consent | The script opens each app's API permissions page in Edge; you click **Grant admin consent**. |
| Notification mailbox | A shared mailbox, with the Worker's Mail.Send scoped to that mailbox only (Exchange RBAC for Applications). |
| Power Platform role | The Worker gets Power Platform reader (a preview API). |
| Teams app | Uploaded to your org catalog and installed for you, with permission to send you activity-feed notices. |
| Copilot seats (`-Steps Seats`) | Adds `LicenseAssignment.Read.All` and `Reports.Read.All` to the Worker and opens its consent page. Creates cloud stand-ins for the on-premises seat groups: `CGW Test - GSGu_MCO_CopilotM365` (you are a member; no license is assigned to it) and one `CGW Test - GSGu_MCO_<LOB>Tracking` group per test LOB. Record them on the seat groups page so the seat read (X13) uses them. |

The script writes the IDs it made to `config\tenant.test.json`: tenant, the two app IDs and service principal
IDs, certificate subjects and thumbprints, group IDs, access package IDs, the catalog, the mailbox and the
Teams app, and the seat stand-in groups. No secrets. The file is git-ignored.

The seat read (X13) needs one tenant setting the script cannot change: in the Microsoft 365 admin center, Settings
› Org settings › Reports, turn off **Display concealed user, group, and site names in all reports**. Otherwise the
Copilot usage report returns hashed names, the read says so, and no seat is proposed for reclaim.

## 2. Create the database

```powershell
pwsh -NoProfile -File src\Governance.Database\Deploy-Database.ps1 -SqlServer localhost -Database CopilotGovernanceTest -CreateSqlLogins -LocalSettingsPath config\appsettings.Local.json -TrustServerCertificate
```

This runs every script in `src\Governance.Database\Scripts` in order. It makes two SQL logins with random
passwords, one for the web (role `app_web`) and one for the worker (role `app_worker`), and writes their
connection strings to `config\appsettings.Local.json` (git-ignored). Neither login is a database owner. In
production the web and worker connect with their own group managed service accounts instead.

## 3. Seed the demo institution

```powershell
dotnet run --project tools\Governance.Seed -- --database CopilotGovernanceTest --sample-requests
```

The seed reads `config\tenant.test.json` from the main checkout by default (`--tenant-settings <path>` to
point elsewhere). It creates:

- 46 fictional people, one per role, with `@example.invalid` mail. Each LOB has a business owner, two
  delegated approvers, a finance partner, a sponsor, a product owner and two makers. Central roles are the
  Platform Owner, Steering, intake, two operators, an architect, a risk reviewer, a FinOps lead and analyst,
  a commercial approver, a sourcing agent, a licensing owner, an auditor and an administrator.
- You, from the tenant settings, as Workbench administrator and auditor only. Administrators cannot decide,
  so you act as the fictional people through the test-only **Act as** switch.
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

## 4. Run it

Use three terminals, in this order:

```powershell
dotnet run --project tools\Governance.MockItsm --urls http://localhost:5470
dotnet run --project src\Governance.Worker
dotnet run --project src\Governance.Web --urls http://localhost:5450
```

Open `http://localhost:5450` in Edge and sign in with your tenant account.

## 5. Try one request end to end

1. Use **Act as** to become Ethan Walsh (Retail Banking developer). On **Start**, choose
   **Build or register an agent**, answer the questions and submit. Keep the estimate under 2,000 credits a
   month, the audience to your team, data internal and the stage Development, so it fits the maker pattern.
2. Act as Aisha Rahman (intake): on **My Work**, check the request and mark it complete.
3. Act as Martin Svensson (architect): classify it.
4. If it fits the maker pattern, act as Marcus Lee (Retail delegated approver) and approve, with a review date.
5. Act as Aisha Rahman again and prepare fulfillment. The ITSM task appears in the mock ITSM.
6. Close the ticket in the mock ITSM as Kevin Brooks (operator). The worker moves the request to verification.
7. Act as Yuki Tanaka (second operator) and verify. The request completes and the workload is in Development.

Each step sends an email from the notification mailbox and a Teams activity notice, once Exchange has
applied the mailbox scope.

## Known limits

- The site runs on `http://localhost`, not HTTPS. The localhost development certificate on this PC is not
  trusted, and trusting it changes a Windows security setting. Entra allows http for localhost. Production
  is HTTPS only.
- The Teams personal tab cannot load: Teams needs HTTPS. Activity notices still arrive and link to the app.
- Exchange can take 30 minutes to 2 hours to apply the mailbox scope. Until then email delivery fails and is
  retried; the delivery log shows it.
- The Power Automate flow that posts cards to Teams channels is not created yet. Channel notices are
  skipped and email stays the notice of record.
- The web and worker run as console processes, not under IIS and a Windows service, so the web farm, the
  shared key ring and the gMSA identities are not exercised here.
