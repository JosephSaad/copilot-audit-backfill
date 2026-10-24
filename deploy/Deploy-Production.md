# Deploying the Workbench to production

The ordered runbook for the DBA team and the Windows administrators. Each step names who does it, the
exact command, and how to check it worked. Repeat the whole runbook for each environment (Development,
Test, Production): each has its own app registrations, certificates, database and notification mailbox.

What you end up with (architecture section 6.4):

- Two or more IIS web nodes behind a load balancer, each running the web application under the web gMSA.
- Two worker nodes running the worker as a Windows Service under the worker gMSA. Every singleton job takes a
  SQL application lock (`sp_getapplock`), so a job never runs on both nodes at once.
- SQL Server 2022 or 2025 in an Always On availability group, Windows authentication only, TDE, encrypted
  connections.
- A Data Protection key ring on an SMB share that only the web gMSA reads, protected by a certificate.
- Outbound HTTPS only, through the proxy. No inbound internet traffic.

All scripts are PowerShell 7 and run from the repository root. Every script that changes the machine accepts
`-WhatIf`; run it that way first.

| Script | What it does |
|---|---|
| `deploy\Test-WorkbenchPrerequisites.ps1` | Checks a server; changes nothing; exit code 1 when a check fails |
| `deploy\Grant-CertificateKeyAccess.ps1` | Lets a gMSA read a certificate's private key |
| `deploy\Install-WorkbenchWeb.ps1` | IIS app pool, site or application, files, web.config, settings, key access, HTTPS binding |
| `deploy\Install-WorkbenchWorker.ps1` | Windows Service, files, settings, key access, proxy, recovery actions |
| `deploy\Save-LedgerDigest.ps1` | Saves ledger digests to WORM storage; `-Verify` checks the database against them |
| `src\Governance.Database\Deploy-Database.ps1` | Creates or upgrades the database (additive, rerunnable scripts) and maps the gMSAs to roles |

## 1. Accounts and certificates (Active Directory and PKI teams)

1. Create three group managed service accounts and allow the right servers to retrieve their passwords:
   - `CORP\gmsa-cgw-web$` for the web nodes,
   - `CORP\gmsa-cgw-worker$` for the worker nodes,
   - optionally `CORP\gmsa-cgw-digest$` for the ledger digest job (step 4.4).

   ```powershell
   New-ADServiceAccount -Name gmsa-cgw-web -DNSHostName gmsa-cgw-web.corp.example -PrincipalsAllowedToRetrieveManagedPassword 'CGW Web Servers'
   ```
   On each server: `Install-ADServiceAccount gmsa-cgw-web` and `Test-ADServiceAccount gmsa-cgw-web` must return `True`.
2. Issue the certificates into `LocalMachine\My` on the servers that use them. Subjects are fixed, so a rotated
   certificate is picked up without a configuration change (Microsoft.Identity.Web selects by distinguished name):

   | Certificate | Subject | Servers | Used for |
   |---|---|---|---|
   | Web sign-in | `CN=CopilotGovernanceWeb` | web nodes | Entra ID client credential of the Web app |
   | Worker | `CN=CopilotGovernanceWorker` | worker nodes | Graph and Power Platform app-only calls |
   | Key ring protection | any subject, by thumbprint | web nodes | Encrypts the Data Protection keys at rest |
   | TLS | the host name | web nodes, load balancer | HTTPS |

   Use 2048-bit RSA or larger, non-exportable keys where the PKI allows it.

## 2. Network (network team)

Allow outbound HTTPS from the web and worker nodes, through the proxy, to:
`login.microsoftonline.com`, `graph.microsoft.com`, `api.powerplatform.com`, the Power Automate endpoint of the
channel flow (step 8.5), and the ITSM endpoint. Nothing inbound from the internet. The load balancer
terminates or passes through TLS 1.2 or later and probes `GET /health/ready` (anonymous, no session affinity
needed).

## 3. Servers (Windows administrators)

Web nodes: Windows Server 2022 or 2025, IIS with the default features and management tools
(`Install-WindowsFeature Web-Server, Web-Mgmt-Tools`), the **ASP.NET Core 10 Hosting Bundle**, PowerShell 7,
SQL command-line tools. Worker nodes: the .NET 10 runtime (or a self-contained publish), PowerShell 7, SQL
command-line tools.

Check each server before installing anything:

```powershell
pwsh -File deploy\Test-WorkbenchPrerequisites.ps1 -Role Web -SqlServer cgw-ag.corp.example -Database CopilotGovernance -GmsaAccount 'CORP\gmsa-cgw-web$' -CertificateSubject 'CN=CopilotGovernanceWeb' -KeyRingPath '\\fs01\CopilotGovernanceKeys$' -ProxyUrl http://proxy.corp.example:8080 -ItsmUrl https://corp.service-now.com
```

```powershell
pwsh -File deploy\Test-WorkbenchPrerequisites.ps1 -Role Worker -SqlServer cgw-ag.corp.example -Database CopilotGovernance -GmsaAccount 'CORP\gmsa-cgw-worker$' -WorkerCertificateSubject 'CN=CopilotGovernanceWorker' -ProxyUrl http://proxy.corp.example:8080 -ItsmUrl https://corp.service-now.com
```

The SQL check runs as you. Step 5 tests it as the gMSA.

## 4. Database (DBA team)

1. **Instance.** SQL Server 2022 (version 16) or 2025 (version 17): the ledger needs 2022 or later. Always On
   availability group with a listener, synchronous commit to the secondary, Windows authentication only, TDE on
   the database, encrypted connections with a certificate the clients trust (the application connects with
   `Encrypt=True;TrustServerCertificate=False;MultiSubnetFailover=True`), encrypted backups.
2. **Create or upgrade the database** as a deployment principal (db_owner on this database only; no
   application identity is ever db_owner):

   ```powershell
   pwsh -File src\Governance.Database\Deploy-Database.ps1 -SqlServer cgw-ag.corp.example -Database CopilotGovernance -WebPrincipal 'CORP\gmsa-cgw-web$' -WorkerPrincipal 'CORP\gmsa-cgw-worker$' -ReaderPrincipal 'CORP\CGW Auditors' -TrustServerCertificate:$false
   ```
   The script runs every file in `src\Governance.Database\Scripts` in name order (each is safe to rerun), then
   maps the web gMSA to `app_web`, the worker gMSA to `app_worker` and the reader group to `gov_reader`.
   **Pass `-TrustServerCertificate:$false`**: its default is meant for the local test server. Never pass
   `-CreateSqlLogins` in production (it makes SQL logins with passwords for the local test only).
   Run it against the listener; then add the database to the availability group and let it seed.
3. **Snapshot isolation for ledger verification.** `sys.sp_verify_database_ledger` refuses to run without it:

   ```sql
   ALTER DATABASE [CopilotGovernance] SET ALLOW_SNAPSHOT_ISOLATION ON;
   ```
4. **Ledger digests on WORM storage.** Create a share on write-once storage (retention lock as long as the
   records schedule). Schedule `deploy\Save-LedgerDigest.ps1` every 4 hours (SQL Server Agent PowerShell step
   or Task Scheduler) as an account with `GENERATE LEDGER DIGEST` and `VIEW LEDGER CONTENT` on the database
   and write access to the share:

   ```powershell
   pwsh -File deploy\Save-LedgerDigest.ps1 -SqlServer cgw-ag.corp.example -Database CopilotGovernance -DigestPath '\\worm01\CopilotGovernanceLedger$'
   ```
   Monthly, and whenever an auditor asks, verify the database against every digest saved so far:

   ```powershell
   pwsh -File deploy\Save-LedgerDigest.ps1 -SqlServer cgw-ag.corp.example -Database CopilotGovernance -DigestPath '\\worm01\CopilotGovernanceLedger$' -Verify
   ```
5. **Test as the gMSA.** Run step 3's prerequisite check once from a scheduled task that runs as the gMSA (the
   task's "Run as" account is the gMSA, no password), with only `-Role` and `-SqlServer`/`-Database`, and read
   its output from a redirected file. It must report SQL as PASS.

## 5. App registrations (Entra administrators)

Per environment, mirror `setup\Setup-TestTenant.ps1`, with these differences:

| | Test (setup script) | Production |
|---|---|---|
| Certificates | `CurrentUser\My`, self-signed | `LocalMachine\My`, issued by the institution's PKI; upload the public part to each app |
| Web redirect URI | `http://localhost:5450/signin-oidc` | `https://<host>/signin-oidc` (and the logout URL `https://<host>/signout-oidc`) |
| App role assignment | the admin gets every role | groups get roles: all users `Workbench.User`; central functions `Workbench.Central`; Platform Operations `Workbench.Operator`; the admin group (PIM for Groups, eligible) `Workbench.Admin`; auditors `Workbench.Auditor` |
| Conditional Access | none | MFA and compliant device on the Web app; an authentication context for high-impact decisions |

1. **Web app**: OIDC sign-in, the five app roles (same stable role IDs as the setup script), delegated
   Microsoft Graph `User.Read`, `openid`, `profile`, `offline_access`, and delegated Power Platform API
   `ResourceQuery.Resources.Read`, `EnvironmentManagement.Environments.Read`, `Licensing.Allocations.Read`,
   `Licensing.BillingPolicies.Read`. Assignment required on the enterprise application. No application
   permissions, no client secret.
2. **Worker app**: Graph application permissions `User.Read.All`, `GroupMember.Read.All`,
   `AccessReview.Read.All`; the **Catalog reader** role on the governed entitlement catalog (narrower than
   `EntitlementManagement.Read.All`); the **Power Platform reader** role at tenant scope (Power Platform RBAC,
   preview: if it fails, licensing reads fall back to operator-run reads). **Do not grant `Mail.Send` in
   Entra**: grants are additive and an Entra grant removes the mailbox scoping of step 3.
3. **Admin consent**: open each app's **API permissions** page in the Entra admin center and select **Grant
   admin consent**.

## 6. Exchange Online (Exchange administrators)

1. Create the shared notification mailbox (no license needed), for example `workbench@corp.example`.
2. Scope the Worker to that one mailbox with RBAC for Applications:

   ```powershell
   New-ServicePrincipal -AppId <worker client ID> -ObjectId <worker enterprise app object ID> -DisplayName 'Copilot Governance Workbench - Worker'
   New-ManagementScope -Name 'CGW notification mailbox' -RecipientRestrictionFilter "PrimarySmtpAddress -eq 'workbench@corp.example'"
   New-ManagementRoleAssignment -Name 'CGW Worker Mail.Send' -App <worker enterprise app object ID> -Role 'Application Mail.Send' -CustomResourceScope 'CGW notification mailbox'
   ```
3. Changes take 30 minutes to 2 hours to apply. Check with
   `Test-ServicePrincipalAuthorization -Identity <worker object ID> -Resource workbench@corp.example`: the role
   must be in scope for the mailbox, and out of scope for any other mailbox you try.

## 7. Teams (Teams administrators)

1. Build the app package with `setup\New-TeamsAppPackage.ps1 -WorkerClientId <worker client ID> -WebBaseUrl https://<host> -Suffix Prod -OutFile <zip>`
   and upload it to the org app catalog.
2. Pre-install it for every Workbench user with a **Teams app setup policy** (the activity feed reaches only
   people who have the app). Allow resource-specific consent for `TeamsActivity.Send.User`.
3. The channel flow: a Power Automate flow with an HTTP request trigger restricted by OAuth to the Worker's
   service principal, posting an Adaptive Card to the Intake, Architecture, Operations, FinOps and Risk
   channels. Give it at least two co-owners (flows are owned by people). Put its URL in the worker's
   `Notifications:ChannelFlowUrl`. Until then, channel notices are skipped and email remains the notice of
   record.

## 8. Install the web application (Windows administrators, each web node)

1. Publish once on the build server: `dotnet publish src\Governance.Web -c Release -o D:\Drop\Web`.
2. Prepare `appsettings.Web.Production.json` from `deploy\appsettings.Web.Production.sample.json`. Replace every
   `<...>` value; the installer refuses a file with placeholders, secrets, or any `Testing` switch turned on.
3. Create the key ring share (for example `\\fs01\CopilotGovernanceKeys$`): NTFS and share **Modify for the web
   gMSA only** (plus administrators). Import the key ring certificate on every web node and put its thumbprint
   in `DataProtection:CertificateThumbprint`. Without one shared ring, a cookie issued by one node is unreadable
   on the other.
4. Install, first with `-WhatIf`:

   ```powershell
   pwsh -File deploy\Install-WorkbenchWeb.ps1 -PublishPath D:\Drop\Web -InstallPath D:\CopilotGovernance\Web -SettingsFile D:\Drop\appsettings.Web.Production.json -GmsaAccount 'CORP\gmsa-cgw-web$' -SiteName 'Copilot Governance Workbench' -HostName workbench.corp.example -TlsCertificateThumbprint <TLS thumbprint> -WhatIf
   ```
   As an application under an existing site, use `-SiteName 'Default Web Site' -ApplicationPath /Workbench`
   instead of `-HostName` and the TLS thumbprint. The installer refuses a path named like a Workbench page
   (`/Admin`, `/Requests` ...), a pool another application uses, a site that already points elsewhere, and
   an install folder it did not create.
5. It ends by calling `/health/live`. Then check `/health/ready` through the load balancer and sign in.

## 9. Install the worker (Windows administrators, each worker node)

1. Publish: `dotnet publish src\Governance.Worker -c Release -r win-x64 -o D:\Drop\Worker`.
2. Prepare `appsettings.Worker.Production.json` from `deploy\appsettings.Worker.Production.sample.json`.
3. The ITSM credential never goes into a file: grant the ITSM integration through the worker certificate if
   the ITSM supports certificate-based OAuth, or store the client secret in the institution's vault and inject
   it as the service environment variable `Itsm__ClientSecret` from the vault's agent.
4. Install, first with `-WhatIf`:

   ```powershell
   pwsh -File deploy\Install-WorkbenchWorker.ps1 -PublishPath D:\Drop\Worker -InstallPath D:\CopilotGovernance\Worker -SettingsFile D:\Drop\appsettings.Worker.Production.json -GmsaAccount 'CORP\gmsa-cgw-worker$' -ProxyUrl http://proxy.corp.example:8080 -WhatIf
   ```
   The service starts delayed-automatic, restarts after 1, 2 and 5 minutes on failure, and gets the proxy as
   service-only environment variables. If it fails with error 1069, give the gMSA "Log on as a service" by
   Group Policy.
5. Check **Admin > Integrations** in the Workbench: every job shows a recent successful run within a few minutes
   (directory sync every 15 minutes, outbox every 5 seconds).

## 10. Open for business (Platform Owner)

Follow the setup sequence of the architecture (section 4.1): approve RACI version 1, activate funding owners,
publish the catalogue and templates, record commercial inventory, set SLAs, run discovery, approve envelopes,
then open intake from **Admin > Intake**.

## Upgrades

1. Read the release notes; schema changes are **additive only** (new tables, new nullable columns, new views),
   so the previous application version keeps working against the upgraded database.
2. Database first: rerun step 4.2 with the same parameters. Every script is safe to rerun.
3. Web nodes one at a time: drain the node at the load balancer, rerun step 8.4 (the installer takes the
   application offline with `app_offline.htm` while it copies, keeps `appsettings.Production.json`), put the
   node back.
4. Worker nodes one at a time: rerun step 9.4 (stops the service, copies, starts it). The other node keeps the
   singleton jobs running meanwhile.

## Rollback

- Application: reinstall the previous drop with the same commands; the database stays (it is backward
  compatible).
- Database: schema changes are additive, so a rollback is rarely needed. If one is, restore the pre-upgrade
  backup on a new database name, compare, and switch the connection strings; never drop ledger tables (they
  cannot be recreated with their history).

## Certificate rotation

1. Issue the new certificate with the **same subject** into `LocalMachine\My` on every node that uses it.
2. Upload its public part to the app registration (keep the old one until step 4).
3. Grant the gMSA access to the new key (a new certificate has a new key file):

   ```powershell
   pwsh -File deploy\Grant-CertificateKeyAccess.ps1 -Subject 'CN=CopilotGovernanceWeb' -Account 'CORP\gmsa-cgw-web$'
   ```
4. Recycle the app pool or restart the worker: the newest valid certificate with the subject is used. Remove the
   old certificate from the app registration and the stores. The key ring certificate is selected by
   thumbprint: add the new one, keep the old one until every key protected by it has expired (90 days).

## What this kit does not do

- It does not create gMSAs, certificates, shares, availability groups or app registrations: those belong to
  their owners and are listed above.
- It was validated with the PowerShell parser, PSScriptAnalyzer and `-WhatIf` runs on a workstation without IIS
  or administrator rights; the IIS and service changes themselves were not run there.
