# The AI gateway, spending limits and agent actions

Three optional tools, each a nightly step once its settings are in place, each skipped with a line in the log when
they are not, and none ever failing the night. Each run is a row of `audit.ExternalUsageRun` (Admin › Health).

| Tool | Reads or does | For |
|---|---|---|
| `tools/Import-GatewayTokenUsage.ps1` | The AI gateway's model tokens by day and business unit (Application Insights, from API Management's token metric policy), and its token limits (the gateway's policies, through Azure Resource Manager) | Leadership › Model tokens by business unit; Forecasting › Spend limits |
| `tools/Import-SpendLimits.ps1` | Microsoft 365 Copilot spending policies (Microsoft Graph beta) and GitHub budgets | Forecasting › Spend limits |
| `tools/Invoke-AgentActions.ps1` | Carries out approved agent actions: quarantine, release from quarantine, a new owner for an orphaned agent, through the Power Platform API; and checks the night after that they still hold | Governance › Agent policies |

The first two only read. The third is the first part of the product that changes Microsoft 365, so it has an app
registration of its own and does nothing until an administrator turns it on.

## The AI gateway's tokens and limits

**What the gateway must do first (the gateway team's, not ours).** The gateway's inbound policy runs
`llm-emit-token-metric` (or `azure-openai-emit-token-metric`) with a dimension that names the business unit: a
cost-center code taken from the calling app's subscription or a header, or the caller's sign-in. Optionally a model
and an agent dimension too. Application Insights must have *custom metrics with dimensions* turned on, or the
dimensions are dropped. Azure Monitor keeps at most 100 values a dimension, so stamp a unit, not a person, where
there are more than 100 callers.

**Values to collect**

| Value | Where | Setting |
|---|---|---|
| The API Management services | Each service's resource ID | `GatewayServices` |
| The Log Analytics workspace behind Application Insights (or the Application Insights resource) | Its workspace ID, or the component's resource ID | `GatewayWorkspaceId` (or `GatewayAppInsights`) |
| The dimensions' names, as the policy writes them | The policy | `GatewayBusinessUnitDimension` (default `Business unit`), `GatewayModelDimension`, `GatewayAgentDimension` |
| The app and certificate (by default the Foundry loader's, then the pipeline's) | Entra ID | `GatewayTenantId`, `GatewayClientId`, `GatewayCertificateThumbprint` |

**Access the app needs:** Log Analytics Reader on the workspace (or Monitoring Reader on the component), and API
Management Service Reader on each service. Nothing else.

**First run**

```powershell
.\tools\Import-GatewayTokenUsage.ps1 -CheckOnly -GatewayService '/subscriptions/<id>/resourceGroups/<rg>/providers/Microsoft.ApiManagement/service/<name>' -WorkspaceId <workspace-id> -SqlServer <server>
.\tools\Import-GatewayTokenUsage.ps1 -GatewayService '/subscriptions/<id>/resourceGroups/<rg>/providers/Microsoft.ApiManagement/service/<name>' -WorkspaceId <workspace-id> -SqlServer <server> -Days 90
```

Then the settings, for the nightly run:

```powershell
# copilot-audit.settings.psd1
    GatewayServices              = @('/subscriptions/<id>/resourceGroups/<rg>/providers/Microsoft.ApiManagement/service/<name>')
    GatewayWorkspaceId           = '<workspace-id>'
    GatewayBusinessUnitDimension = 'Business unit'
```

**Business units.** A business unit is an L2 and L3 executive pair: the people at levels 2 and 3 of the org chart
(`TokenUnitLevels`, `2,3`). A value the gateway stamps that is a person's sign-in is put in that person's unit with no
more work. Any other value (a cost-center code) is routed once by an administrator on Leadership › Model tokens by
business unit, to an L2 executive and, where it is one L3's, the L3 within it; a route is dated, so a change keeps the
months before it. The page lists every value in no business unit. Counts only: what the models cost is on AI spend
across Azure.

## Spending limits

**Copilot spending policies (Microsoft Graph beta).** The pipeline's app needs the application permissions
`CopilotCostManagement-Policy.Read.All` (the policies) and `CopilotCostManagement-Assignment.Read.All` (the groups a
policy applies to), with admin consent. The API is in beta: a change in its shape is a note in the run, never a
failure; the last load is kept.

**GitHub budgets.** The GitHub loader's token, with the fine-grained permission *Enterprise billing: read* (an
enterprise owner or billing manager), and `GitHubEnterprise`. Budgets exist only for an enterprise.

**The gateway's token limits** come with its tokens (above): each `llm-token-limit` or `azure-openai-token-limit` at
the service, a product or an API.

**First run, then the setting**

```powershell
.\tools\Import-SpendLimits.ps1 -CheckOnly -SqlServer <server>
.\tools\Import-SpendLimits.ps1 -SqlServer <server>
```

```powershell
# copilot-audit.settings.psd1
    SpendLimitSources = @('Graph', 'GitHub')
```

Forecasting › Spend limits shows every limit beside its spend, the ones projected to be reached, and next quarter's
suggested values. Limits no loader reads (an environment's Copilot Credit limit set in the Power Platform admin
center) are entered there. **The alert** *A spend limit will be reached* goes, once a day while a limit is reached or
projected to be, to the addresses in `SpendLimitAlertTo`; once `SpendLimitAlertToExecutives` is 1, also to each
limit's L2 and L3 executives. Both are set on the page.

## Agent actions (off by default)

**What it changes.** Only what an administrator approved on Governance › Agent policies: quarantine an agent, release
it, or give an orphaned agent to a new owner. It never deletes an agent. Notify (an email to the agent's owner) is
sent by the dashboard itself and needs none of this.

**Its own app registration**, separate from the audit app, with a certificate:

| Value | Setting |
|---|---|
| The app and certificate | `AgentActionsTenantId` (default `TenantId`), `AgentActionsClientId`, `AgentActionsCertificateThumbprint` |
| Power Platform API permission | `CopilotStudio.AdminActions.Invoke` |
| Microsoft Graph permission (only to find a new owner by sign-in) | `User.Read.All` |

**Check before turning it on.** Microsoft documents these calls for an administrator's own sign-in; whether the app's
sign-in is accepted, and whether the app must first be registered as a Power Platform management app, is to be
confirmed in your tenant:

```powershell
.\tools\Invoke-AgentActions.ps1 -CheckOnly -SqlServer <server>
.\tools\Invoke-AgentActions.ps1 -WhatIf -SqlServer <server>
```

`-CheckOnly` signs in, lists the environments, reads one agent's quarantine state, and says whether the token carries
the permission. `-WhatIf` lists what tonight's run would do and does none of it. Then turn it on: the dashboard
setting `AgentActionsEnabled` = `1` (dash.Setting), and the three settings above in the settings file. Each night
the run carries out up to 50 approved actions (`-MaxActions`), and a second step (`-Check`) confirms the last week's
quarantines and releases still hold.
