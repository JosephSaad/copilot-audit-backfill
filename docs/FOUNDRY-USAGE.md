# Foundry and Azure OpenAI usage: setup

How to load the usage and cost of **Microsoft Foundry** and **Azure OpenAI** model deployments into the Copilot database, with `tools\Import-FoundryUsage.ps1`. This is the first plumbing (2.6.2): the tables, the load and its log. No dashboard report reads the tables yet; a reporting tool can.

Two people are involved. An **Azure administrator** who can assign roles on the subscriptions does part 2, which needs no access to the job server or to SQL Server: hand them that part as it is. The **job server's administrator** does the rest.

The loader has been run end to end against a stand-in for Azure's APIs that answers as Microsoft documents them. It has **not** been run against Azure itself: check the first real load against the Azure portal (part 5).

## 1. The values needed

Collect these before starting. The last column says who has each.

| Value | Example | Where it goes | Who has it |
|---|---|---|---|
| Subscription ids, one for each subscription that holds Foundry or Azure OpenAI resources | `1f2e3d4c-0000-0000-0000-000000000000` | `FoundrySubscriptions` in `copilot-audit.settings.psd1` | The Azure administrator: portal › **Subscriptions**. |
| The app registration's client id | `9a8b7c6d-0000-0000-0000-000000000000` | Already in the settings as `ClientId`, when the pipeline's own app is used (the usual case) | The job server's administrator; the Azure administrator needs it for part 2. |
| The tenant | `contoso.onmicrosoft.com` | Already in the settings as `TenantId` | The same. |
| The certificate's thumbprint | `A1B2...` (40 characters) | Already in the settings as `CertificateThumbprint` | The same. |

**Which app signs in.** By default the loader signs in as the app registration the pipeline already uses to read the audit log, with the same certificate. Nothing new is created: the Azure administrator gives that app two reader roles. This is the simplest setup, and the one to choose unless one of these holds:

- **The subscriptions are in another Entra tenant** than the one the audit log is read from. An app signs in to one tenant, so an app registration is needed there.
- **Policy wants a separate identity** for Azure than for Microsoft Graph.

Then three more values are needed, all from the tenant the subscriptions are in:

| Value | Where it goes |
|---|---|
| That tenant's id or domain | `FoundryTenantId` |
| The client id of an app registration in it | `FoundryClientId` |
| The thumbprint of a certificate on the job server whose public key is uploaded to that app | `FoundryCertificateThumbprint` |

Part 3, step 2 makes the certificate.

## 2. For the Azure administrator

*This part can be sent as it is. Fill in the client id first.*

We load the usage and cost of our Foundry and Azure OpenAI model deployments into our own reporting database once a night, read-only. The load signs in as an app registration, client id **`<client id>`**, and needs two roles on each subscription that holds those resources. Assigning them needs **Owner**, **User Access Administrator** or **Role Based Access Control Administrator** on the subscription.

**1. Assign two roles, at the subscription**, to that app (a service principal; search for it by name or client id under *Select members*):

| Role | What the load reads with it |
|---|---|
| **Monitoring Reader** | The Azure AI accounts, their model deployments, and their metrics: requests and tokens per deployment per day |
| **Cost Management Reader** | The daily cost of those accounts, by meter |

In the portal: **Subscriptions** › the subscription › **Access control (IAM)** › **Add** › **Add role assignment**, once for each role. Or with the Azure CLI, for each subscription:

```bash
az role assignment create --assignee <client id> --role "Monitoring Reader" --scope /subscriptions/<subscription id>
```

```bash
az role assignment create --assignee <client id> --role "Cost Management Reader" --scope /subscriptions/<subscription id>
```

If listing the accounts is later refused, add **Reader** the same way; Monitoring Reader should be enough. From 3.3.0, Reader also lets the load read each account's content filters, and Reader on the organization's other subscriptions lets it list the AI accounts outside `FoundrySubscriptions` (optional: without it, those are simply not listed).

**2. Send us** the subscription ids, and confirm which tenant they are in.

**What the roles allow.** Reading resource names and settings, metrics and cost. They do not allow reading an account's keys, calling a model, reading prompts or responses, or changing anything.

**What is read, and how often.** Once a night, from Azure Resource Manager, Azure Monitor and Cost Management: the list of Azure AI accounts of kind *OpenAI* and *AIServices* and their deployments (model, version, SKU, capacity); per deployment and day, the number of requests and of input and output tokens; per account, meter and day, the cost.

**If the subscriptions are in another tenant than our app**, we also need an app registration in that tenant: create one (no API permissions, no redirect address, no secret), send us its client id and the tenant's id, upload the certificate (`.cer`) we send you under **Certificates & secrets** › **Certificates**, and assign the two roles to it instead.

**To withdraw access at any time**, remove the two role assignments.

A new role assignment can take up to half an hour to work. Cost for a day appears in Cost Management 8 to 24 hours after it.

## 3. On the job server

As an administrator, in PowerShell 7, from the folder the scripts are in (the one with `Invoke-CopilotPipeline.ps1`).

**1. The tables.** They come with the release: `Install-CopilotAudit.ps1` creates them. If the database was upgraded to 2.6.2, they are there.

**2. Only for an app of its own: make its certificate.** Skip this when the pipeline's app is used.

```powershell
$c = New-SelfSignedCertificate -Subject 'CN=Copilot Foundry usage' -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 2048 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2)
```

```powershell
Export-Certificate -Cert $c -FilePath .\foundry-usage.cer; $c.Thumbprint
```

Send `foundry-usage.cer` (the public key only) to the Azure administrator to upload to the app. Give the account the task *Copilot audit - nightly* runs as read access to the private key: `certlm.msc` › **Personal** › **Certificates** › the certificate › **All Tasks** › **Manage Private Keys**. Keep the thumbprint for step 4.

**3. Load the history once.** Azure Monitor keeps 93 days of metrics, so what is not loaded within that is gone:

```powershell
.\tools\Import-FoundryUsage.ps1 -SubscriptionId 1f2e3d4c-0000-0000-0000-000000000000 -SqlServer SQLPROD01 -Days 90
```

Give several subscriptions separated by commas. The tenant, client id and thumbprint come from the settings file; add `-TenantId`, `-ClientId` and `-CertificateThumbprint` for an app of its own, `-Database` if it is not `CopilotAudit`, and `-TrustServerCertificate` if the nightly run uses it. Run it as an account that can read the certificate's private key. Its last line says what was loaded:

```
Succeeded. Loaded 6 account(s), 14 deployment(s), 733 usage row(s), 1208 cost row(s) for 90 day(s).
```

**4. Turn on the nightly load.** Add to `copilot-audit.settings.psd1`:

```powershell
    FoundrySubscriptions = @('1f2e3d4c-0000-0000-0000-000000000000', '2a3b4c5d-0000-0000-0000-000000000000')
    # Only for an app of its own:
    # FoundryTenantId              = 'ai.contoso.com'
    # FoundryClientId              = '9a8b7c6d-0000-0000-0000-000000000000'
    # FoundryCertificateThumbprint = 'A1B2C3...'
```

From the next night, the run has a last step, *Foundry usage and cost*, which loads the last 10 days again (cost for recent days is still settling, and an open month's cost is an estimate until five days after it ends). Without `FoundrySubscriptions` in the settings, the run skips the step and says so in its log; nothing fails.

**The step is optional to the run.** If it fails (an expired certificate, a role removed), the failure is on its own step row and in `audit.ExternalUsageRun`, and the rest of the night, already done by then, stands as succeeded.

**The certificate.** With the pipeline's app, its certificate is already tracked on Admin › Health and renewed as before. A certificate of the loader's own is tracked there too, as *Foundry usage app certificate*, with the same expiry alerts. To renew it: step 2 again, the new `.cer` uploaded to the app, and the new thumbprint in `FoundryCertificateThumbprint`.

**Network.** The job server needs outbound HTTPS to `login.microsoftonline.com` and `management.azure.com`.

## 4. What it fills

| Table | One row per | Kept how |
|---|---|---|
| `audit.FoundryDeployment` | Model deployment: subscription, resource group, account, deployment, kind, region, model, version, SKU, capacity | Replaced whole for each subscription each run: it is the deployments as they are now |
| `audit.FoundryUsageDay` | Deployment, model and UTC day: requests, input, output and total tokens | The days loaded are written again; older days stay, beyond Azure Monitor's 93 |
| `audit.FoundryCostDay` | Account, meter and UTC day: cost before tax, and its currency | The same |
| `audit.FoundryAccount` (3.3.0) | Account: public network access, the default for other networks, private endpoints, whether keys are refused, its managed identity, its logging (diagnostic settings and their categories), and its tags, owner, cost center and app read from them | Replaced whole for each subscription each run |
| `audit.FoundryContentFilter` (3.3.0) | Account and content filter: its base, its mode, whether any filter is looser than Microsoft's default; each deployment's filter is on `audit.FoundryDeployment` (`RaiPolicy`) | The same |
| `audit.FoundryModelCatalog` (3.3.0) | Region, deployed model and version: its lifecycle and the day it retires, and a SKU that retires sooner | Replaced for each region each run |
| `audit.FoundryAccountSeen` (3.3.0) | Every Azure OpenAI and Foundry account the app can see, in any subscription, and whether its subscription is loaded | Replaced whole each run |
| `audit.ExternalUsageRun` | Run: when, the days covered, status, rows, what was loaded or why not | Every run kept |

[SCHEMA.md](SCHEMA.md#ai-usage-outside-microsoft-365-copilot-262) has every column.

**What it does not give.**

- **Who made a request.** Azure Monitor's metrics have no user, key or calling application. Use is by account, deployment and model. Use per person needs the calls to go through Azure API Management, or the accounts' diagnostic logs; neither is read here.
- **Cost per deployment.** Cost Management reports by resource and meter (*gpt-4o Input Tokens*, say). Two deployments of one model in one account share a meter. A provisioned (PTU) deployment's daily use of its capacity is on `audit.FoundryUsageDay` from 3.3.0 (`PtuUtilizationAvg`, `PtuUtilizationMax`).
- **Cost per app.** Cost is by account. Give each app an account of its own, or tag the accounts (`app`, `owner`, `cost-center`), and the pages can group cost by tag; apps sharing one account share its cost.
- **Other Azure AI services.** Only accounts of kind *OpenAI* and *AIServices* are read; Speech, Vision and the rest are left out, of use and of cost.
- **Accounts that report neither set of metrics.** The loader reads the current model metrics (`ModelRequests`, `InputTokens`, `OutputTokens`, `TotalTokens`) and, for an account with none, the older Azure OpenAI ones. A deployment type that reports under other names loads its cost and no usage.

Days are UTC days, not the reporting time zone's.

## 5. Checking it

**Before the first load**, try each read without writing anything:

```powershell
.\tools\Import-FoundryUsage.ps1 -CheckOnly
```

One line a part: OK, MISSING (the load needs it, and the role to give on which subscription) or NOTE (optional, such as logging, content filters, or AI accounts in subscriptions not listed). The exit code is 0 when everything the load needs can be read. No database is needed.

**The 3.3.0 parts** (posture, logging, content filters, retirement, provisioned use, accounts seen) are read on their own: one Azure refuses is named in the run's detail (*Not read (optional)*), and the run still ends Succeeded.

**Admin › Health**, panel *Other AI usage: GitHub Copilot and Foundry*: when it last loaded, the days and rows, and the newest day held. It turns amber for a partial load or no load for two days; red when the last run failed, with Azure's own message.

**In SQL:**

```sql
-- The last runs
SELECT TOP (5) StartedUtc, Status, Scope, FromDay, ToDay, RowsWritten, Detail
FROM audit.ExternalUsageRun WHERE Source = 'Foundry' ORDER BY RunId DESC;

-- What is deployed
SELECT Account, Deployment, ModelName, ModelVersion, SkuName, Capacity, Location
FROM audit.FoundryDeployment ORDER BY Account, Deployment;

-- Requests and tokens by model, last 30 days
SELECT ModelName, SUM(Requests) AS Requests, SUM(InputTokens) AS InputTokens, SUM(OutputTokens) AS OutputTokens
FROM audit.FoundryUsageDay WHERE [Day] >= DATEADD(DAY, -30, CAST(SYSUTCDATETIME() AS DATE))
GROUP BY ModelName ORDER BY Requests DESC;

-- Cost by month and meter
SELECT DATEFROMPARTS(YEAR([Day]), MONTH([Day]), 1) AS [Month], Meter, SUM(Cost) AS Cost, MAX(Currency) AS Currency
FROM audit.FoundryCostDay GROUP BY DATEFROMPARTS(YEAR([Day]), MONTH([Day]), 1), Meter ORDER BY [Month] DESC, Cost DESC;

-- Cost beside use, by account and day: each side summed first, since meters and deployments do not pair one to one
SELECT u.Account, u.[Day], u.Requests, u.Tokens, c.Cost, c.Currency
FROM (SELECT SubscriptionId, ResourceGroup, Account, [Day], SUM(Requests) AS Requests, SUM(TotalTokens) AS Tokens
      FROM audit.FoundryUsageDay GROUP BY SubscriptionId, ResourceGroup, Account, [Day]) AS u
LEFT JOIN (SELECT SubscriptionId, ResourceGroup, Account, [Day], SUM(Cost) AS Cost, MAX(Currency) AS Currency
           FROM audit.FoundryCostDay GROUP BY SubscriptionId, ResourceGroup, Account, [Day]) AS c
  ON c.SubscriptionId = u.SubscriptionId AND c.ResourceGroup = u.ResourceGroup AND c.Account = u.Account AND c.[Day] = u.[Day]
ORDER BY u.[Day] DESC, u.Account;
```

**Against Azure**, after the first real load: one account's requests and tokens for a day against its **Metrics** blade (the same metric, split by deployment, daily, UTC); a month's cost against **Cost analysis** filtered to the account, actual cost, grouped by meter. Report a difference with the run's `Detail`.

## 6. When it does not work

| The run's detail or the error says | It means | Do |
|---|---|---|
| `No certificate ... in LocalMachine\My or CurrentUser\My` | The thumbprint in the settings is not on this computer | Check `CertificateThumbprint` or `FoundryCertificateThumbprint` |
| `has no RSA private key this account can use` | The account running it cannot read the private key | *Manage Private Keys* (part 3, step 2) |
| `AADSTS700027` or `AADSTS700016` when signing in | The certificate is not uploaded to that app, or the client id or tenant is wrong | Upload the `.cer`; check the three values |
| `AuthorizationFailed` on deployments | The app lacks Monitoring Reader on that subscription, or it was assigned minutes ago | Part 2, step 1; wait half an hour |
| `AuthorizationFailed` or `RBACAccessDenied` on cost only | The app lacks Cost Management Reader | The same |
| Cost not loaded, with a message about the offer or the agreement | Cost Management does not serve that subscription type to this API | Usage still loads; the run ends Partial |
| Deployments load, usage rows are 0 | Nothing was called in the days loaded, or the accounts report under other metric names | Look at the account's **Metrics** blade |
| `Azure asked to wait ... (throttled)` in the log | Cost Management limits how often it is asked | Nothing: it waits and asks again, up to three times |
| `The tables are not in ... yet` | The database is from before 2.6.2 | `Install-CopilotAudit.ps1` from this release |

A run that ends **Partial** loaded what it could: its detail names the subscription and the part it could not read, and the next run tries all of it again. One subscription the app cannot read does not stop the others.

## 7. Parameters

`tools\Import-FoundryUsage.ps1`, all optional when the settings file has them:

| Parameter | Default | Use |
|---|---|---|
| `-SubscriptionId` | `FoundrySubscriptions` in the settings | The subscriptions to read |
| `-Days` | 10 | How many complete UTC days to load, ending yesterday; up to 90 |
| `-TenantId` | `FoundryTenantId`, else `TenantId` | The tenant to sign in to |
| `-ClientId` | `FoundryClientId`, else `ClientId` | The app registration |
| `-CertificateThumbprint` | `FoundryCertificateThumbprint`, else `CertificateThumbprint` | Its certificate, in LocalMachine\My or CurrentUser\My |
| `-SqlServer`, `-Database`, `-TrustServerCertificate` | From the settings; database `CopilotAudit` | Where to write |
| `-ConnectionString` | None | In place of the three above |
| `-ArmEndpoint`, `-LoginEndpoint` | `https://management.azure.com`, `https://login.microsoftonline.com` | For a sovereign cloud |
| `-SettingsPath` | `copilot-audit.settings.psd1` in the scripts' folder | Another settings file |
| `-CheckOnly` | | Tries each read once and writes nothing; exit code 0 when everything the load needs can be read (3.3.0) |
