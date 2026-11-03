# Deploying the Governance Workbench to production

The Workbench is part of Copilot Insights: its pages are served by the Insights site under `/workbench`, and
`dashboard\Install-CopilotInsights.ps1` installs it with `-GovernanceDatabase`. This is the ordered runbook for
the teams around that install. Each step names who does it, the exact command, and how to check it worked. Run
the commands from the folder of the release's source package (or the repository). [docs\DEPLOYMENT.md](../../docs/DEPLOYMENT.md)
covers installing Copilot Insights itself.

What you end up with:

- The Copilot Insights site on one or more IIS web servers (behind a load balancer when there are several), each
  serving `/workbench` under the site's own app pool account and sign-in (Windows, or Entra ID).
- The Workbench worker as a Windows service (`CopilotGovernanceWorker`) under its own gMSA, on **one** server,
  the one installed with `-WorkerServer`. Every scheduled job also takes a SQL application lock
  (`sp_getapplock`), so a second copy started by mistake waits rather than running a job twice.
- The `CopilotGovernance` database on SQL Server 2022 or later, beside the Copilot database on the same instance,
  Windows authentication only, encrypted connections.
- A daily ledger digest saved to write-once storage by a scheduled task on the worker's server.
- Outbound HTTPS only, through the proxy. No inbound internet traffic.

The Workbench's own scripts are PowerShell 7; the installer itself runs in Windows PowerShell 5.1 and calls them
with `pwsh`. Every script that changes the machine accepts `-WhatIf`; run it that way first.

| Script | What it does |
|---|---|
| `dashboard\Install-CopilotInsights.ps1 -GovernanceDatabase` | The site's Workbench settings, the database, and with `-WorkerServer` the worker and the digest task |
| `governance\deploy\Test-WorkbenchPrerequisites.ps1` | Checks a server; changes nothing; exit code 1 when a check fails. The installer runs it first |
| `governance\src\Governance.Database\Deploy-Database.ps1` | Creates or upgrades the database (additive, rerunnable scripts) and maps accounts to its roles |
| `governance\deploy\Install-WorkbenchWorker.ps1` | The Windows service, files, settings, key access, proxy, recovery actions. The installer runs it |
| `governance\deploy\Grant-CertificateKeyAccess.ps1` | Lets the worker's gMSA read its certificate's private key, after a rotation |
| `governance\deploy\Save-LedgerDigest.ps1` | Saves a ledger digest to WORM storage; `-Verify` checks the database against every one saved |

## 1. Accounts and certificates (Active Directory and PKI teams)

1. Create a group managed service account for the worker, `CORP\gmsa-cgw-worker$`, and allow the worker's server
   to retrieve its password:

   ```powershell
   New-ADServiceAccount -Name gmsa-cgw-worker -DNSHostName gmsa-cgw-worker.corp.example -PrincipalsAllowedToRetrieveManagedPassword 'CGW Worker Servers'
   ```
   On that server: `Install-ADServiceAccount gmsa-cgw-worker` and `Test-ADServiceAccount gmsa-cgw-worker` must
   return `True`. Give it **Log on as a service** and **Log on as a batch job** (the digest task) by Group Policy.
   The site needs no account of its own for the Workbench: it uses the app pool's account Insights already has.
2. Issue the worker certificate, subject `CN=CopilotGovernanceWorker`, into `LocalMachine\My` on the worker's
   server: 2048-bit RSA or larger, non-exportable key where the PKI allows it. It signs the worker in to Graph and
   the Power Platform. The subject is fixed, so a rotated certificate is picked up without a configuration change.

## 2. Network (network team)

Allow outbound HTTPS from the web servers and the worker's server, through the proxy, to
`login.microsoftonline.com`, `graph.microsoft.com`, `api.powerplatform.com`, the Power Automate endpoint of the
channel flow (step 7.3) and the ITSM endpoint. Nothing inbound from the internet. The load balancer's probe stays
Insights' `/healthz`.

## 3. Servers (Windows administrators)

Every web server that gets `-GovernanceDatabase`: what Copilot Insights needs (IIS, the ASP.NET Core 10 Hosting
Bundle), plus **PowerShell 7** and the **SQL Server command-line tools** (`sqlcmd`). The worker's server is one of
the web servers; the Hosting Bundle brings the .NET 10 runtime the worker runs on. Building from source needs the
.NET 10 SDK there too; with the release's `copilot-governance-worker-vX.Y.Z.zip` it does not.

The installer checks each server before it changes the site. To check one by hand first:

```powershell
pwsh -File governance\deploy\Test-WorkbenchPrerequisites.ps1 -Role Web -SqlServer SQLPROD01 -Database CopilotGovernance -ProxyUrl http://proxy.corp.example:8080
```

```powershell
pwsh -File governance\deploy\Test-WorkbenchPrerequisites.ps1 -Role Both -SqlServer SQLPROD01 -Database CopilotGovernance -GmsaAccount 'CORP\gmsa-cgw-worker$' -WorkerCertificateSubject 'CN=CopilotGovernanceWorker' -ProxyUrl http://proxy.corp.example:8080 -ItsmUrl https://corp.service-now.com
```

Add `-TrustServerCertificate` when SQL Server has its own self-signed certificate. A SQL Server older than 2022 is
a FAIL: the governance database's ledger tables need 2022 (version 16) or later.

## 4. Database (DBA team)

1. **Instance.** SQL Server 2022 or later, Windows authentication only, encrypted connections. At the client, the
   instance that holds the Copilot database holds this one too.
2. **Create or upgrade the database.** The installer does it when run with `-GrantDatabaseAccess` (it runs
   `Deploy-Database.ps1`, maps the site's app pool account to `app_web` and `-WorkerAccount` to `app_worker`).
   Without `-GrantDatabaseAccess` it prints the command for a DBA, such as:

   ```powershell
   pwsh -File governance\src\Governance.Database\Deploy-Database.ps1 -SqlServer SQLPROD01 -Database CopilotGovernance -WebPrincipal 'CORP\gmsa-insights$' -WorkerPrincipal 'CORP\gmsa-cgw-worker$'
   ```
   It runs every file in `governance\src\Governance.Database\Scripts` in name order (each is safe to rerun) as a
   deployment principal (db_owner on this database only; no application account is ever db_owner). Add
   `-ReaderPrincipal 'CORP\CGW Auditors'` to give a group read access through `gov_reader`. Never pass
   `-CreateSqlLogins` in production (SQL logins with passwords, for the local test only).
3. **Snapshot isolation for ledger verification.** `sys.sp_verify_database_ledger` refuses to run without it:

   ```sql
   ALTER DATABASE [CopilotGovernance] SET ALLOW_SNAPSHOT_ISOLATION ON;
   ```
4. **Ledger digests on WORM storage.** Create a share on write-once storage (retention lock as long as the records
   schedule) that the worker's gMSA can write to, and give it to the installer as `-LedgerDigestPath`. The scheduled
   task *Copilot Governance ledger digest* then runs `Save-LedgerDigest.ps1` daily at `-LedgerDigestAt` (02:00
   unless given) as the worker's gMSA, which, as a user of the database, may generate digests. Monthly, and
   whenever an auditor asks, verify the database against every digest saved so far, as an account with
   `VIEW LEDGER CONTENT`:

   ```powershell
   pwsh -File governance\deploy\Save-LedgerDigest.ps1 -SqlServer SQLPROD01 -Database CopilotGovernance -DigestPath '\\worm01\CopilotGovernanceLedger$' -Verify
   ```
5. **Test as the gMSA.** Run step 3's check once from a scheduled task that runs as the gMSA (the task's "Run as"
   account is the gMSA, no password), with only `-Role`, `-SqlServer` and `-Database`, and read its output from a
   redirected file. It must report SQL as PASS.

## 5. App registration (Entra administrators)

Sign-in to the site is Insights' (Windows, or Entra ID as in [docs\ENTRA-SIGN-IN.md](../../docs/ENTRA-SIGN-IN.md)),
and Workbench roles are Insights grants. The worker needs one app registration of its own, mirroring
`governance\setup\Setup-TestTenant.ps1`'s worker app:

1. Graph application permissions `User.Read.All`, `GroupMember.Read.All`, `AccessReview.Read.All`; the **Catalog
   reader** role on the governed entitlement catalog (narrower than `EntitlementManagement.Read.All`); the **Power
   Platform reader** role at tenant scope (Power Platform RBAC, preview: if it fails, licensing reads fall back to
   operator-run reads). **Do not grant `Mail.Send` in Entra**: grants are additive and an Entra grant removes the
   mailbox scoping of step 6.
2. Upload the public part of the worker certificate (step 1.2). No client secret.
3. **Admin consent**: open the app's **API permissions** page in the Entra admin center and select **Grant admin
   consent**.

## 6. Exchange Online (Exchange administrators)

1. Create the shared notification mailbox (no license needed), for example `workbench@corp.example`.
2. Scope the worker to that one mailbox with RBAC for Applications:

   ```powershell
   New-ServicePrincipal -AppId <worker client ID> -ObjectId <worker enterprise app object ID> -DisplayName 'Copilot Governance Workbench - Worker'
   New-ManagementScope -Name 'CGW notification mailbox' -RecipientRestrictionFilter "PrimarySmtpAddress -eq 'workbench@corp.example'"
   New-ManagementRoleAssignment -Name 'CGW Worker Mail.Send' -App <worker enterprise app object ID> -Role 'Application Mail.Send' -CustomResourceScope 'CGW notification mailbox'
   ```
3. Changes take 30 minutes to 2 hours to apply. Check with
   `Test-ServicePrincipalAuthorization -Identity <worker object ID> -Resource workbench@corp.example`: the role must
   be in scope for the mailbox, and out of scope for any other mailbox you try.

## 7. Teams (Teams administrators)

1. Build the app package with the site's address (its links add `/workbench`):
   `pwsh -File governance\setup\New-TeamsAppPackage.ps1 -WorkerClientId <worker client ID> -WebBaseUrl https://insights.corp.example -Suffix Prod -OutFile <zip>`,
   and upload it to the org app catalog.
2. Pre-install it for every Workbench user with a **Teams app setup policy** (the activity feed reaches only people
   who have the app). Allow resource-specific consent for `TeamsActivity.Send.User`.
3. The channel flow: a Power Automate flow with an HTTP request trigger restricted by OAuth to the worker's service
   principal, posting an Adaptive Card to the Intake, Architecture, Operations, FinOps and Risk channels
   (`governance\setup\CHANNEL-FLOW.md`). Give it at least two co-owners (flows are owned by people). Its URL goes in
   the worker's `Notifications:ChannelFlowUrl` (step 8.3). Until then, channel notices are skipped and email
   remains the notice of record.

## 8. Install (Windows administrators)

1. **The worker's settings.** Make `appsettings.Worker.Production.json` from
   `governance\deploy\appsettings.Worker.Production.sample.json` and replace every `<...>` value except the two the
   installer fills in (`ConnectionStrings:GovernanceWorker` and `Notifications:WorkbenchBaseUrl`). The worker's
   installer refuses a file with placeholders left, secrets, or any `Testing` switch turned on.
2. **The site's Workbench settings.** The installer writes `ConnectionStrings:Governance` and
   `Notifications:WorkbenchBaseUrl` into the site's `appsettings.Production.json`. Add the `Tenant`, `Workbench` and
   `Itsm` sections from `governance\deploy\appsettings.Web.Production.sample.json` to that file by hand on each web
   server (every update keeps it), then recycle the app pool.
3. **Secrets.** The ITSM credential and the channel flow URL never go into a file: set them as machine environment
   variables for the worker's service (`Itsm__Password`, `Notifications__ChannelFlowUrl`), or have the
   institution's vault agent inject them.
4. **The server that runs the worker**, first with `-WhatIf`, in Windows PowerShell 5.1 as an administrator:

   ```powershell
   .\dashboard\Install-CopilotInsights.ps1 -PackagePath .\copilot-insights-vX.Y.Z.zip -SqlServer SQLPROD01 -GrantDatabaseAccess `
       -GovernanceDatabase CopilotGovernance -SiteUrl https://insights.corp.example `
       -WorkerServer -WorkerAccount 'CORP\gmsa-cgw-worker$' -WorkerPackagePath .\copilot-governance-worker-vX.Y.Z.zip `
       -WorkerSettingsFile D:\Drop\appsettings.Worker.Production.json -LedgerDigestPath '\\worm01\CopilotGovernanceLedger$' `
       -ProxyUrl http://proxy.corp.example:8080 -WhatIf
   ```
   Without `-WorkerPackagePath`, the worker is published from `governance\src\Governance.Worker` (needs the .NET 10
   SDK). The service starts delayed-automatic as the gMSA, restarts after 1, 2 and 5 minutes on failure, and gets
   the proxy as service-only environment variables. If it fails with error 1069, give the gMSA "Log on as a
   service" by Group Policy.
5. **Every other web server**: the same command without `-WorkerServer` and the worker's parameters:

   ```powershell
   .\dashboard\Install-CopilotInsights.ps1 -PackagePath .\copilot-insights-vX.Y.Z.zip -SqlServer SQLPROD01 -GrantDatabaseAccess `
       -GovernanceDatabase CopilotGovernance -SiteUrl https://insights.corp.example -WorkerAccount 'CORP\gmsa-cgw-worker$'
   ```
6. **Check.** Open `https://insights.corp.example/workbench`. In **Admin > Integrations** of the Workbench every job
   shows a recent successful run within a few minutes (directory sync every 15 minutes, outbox every 5 seconds).

## 9. Open for business (Platform Owner)

Follow the setup sequence of the architecture (section 4.1): approve RACI version 1, activate funding owners,
publish the catalog and templates, record commercial inventory, set SLAs, run discovery, approve envelopes, then
open intake from **Admin > Intake**.

## Upgrades

1. Read the release notes; schema changes are **additive only** (new tables, new nullable columns, new views), so
   the previous version keeps working against the upgraded database.
2. Web servers one at a time: drain the server at the load balancer, rerun step 8.5 (or 8.4 on the worker's
   server) with the new packages, put it back. The first run with `-GrantDatabaseAccess` upgrades the database;
   every script is safe to rerun. Run again without `-WorkerSettingsFile`, the worker keeps its installed settings.

## Rollback

- Application: reinstall the previous packages with the same commands; the database stays (it is backward
  compatible).
- Database: schema changes are additive, so a rollback is rarely needed. If one is, restore the pre-upgrade backup
  on a new database name, compare, and switch the connection strings; never drop ledger tables (they cannot be
  recreated with their history).

## Certificate rotation

1. Issue the new worker certificate with the **same subject** into `LocalMachine\My` on the worker's server.
2. Upload its public part to the app registration (keep the old one until step 4).
3. Grant the gMSA access to the new key (a new certificate has a new key file):

   ```powershell
   pwsh -File governance\deploy\Grant-CertificateKeyAccess.ps1 -Subject 'CN=CopilotGovernanceWorker' -Account 'CORP\gmsa-cgw-worker$'
   ```
4. Restart the worker service: the newest valid certificate with the subject is used. Remove the old certificate
   from the app registration and the store.

## What this kit does not do

- It does not create gMSAs, certificates, shares or app registrations: those belong to their owners and are listed
  above.
- The installer's Workbench part was checked with the PowerShell parser, PSScriptAnalyzer and its own functions'
  tests; the IIS, service and scheduled-task changes themselves were not run on a server in that check.
