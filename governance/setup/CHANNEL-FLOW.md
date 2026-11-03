# Teams queue channels: the Power Automate flow

The Workbench posts a card to a team queue's Teams channel (Intake, Architecture, Operations, FinOps, Risk) when work arrives there (architecture X11). Microsoft 365 Connectors are retiring, so the card goes through a Power Automate flow with an HTTP trigger. Only the Workbench Worker can call it: the trigger is restricted by OAuth to the Worker's service principal.

Email stays the notice of record. If the flow is down, people still get email.

## What the script does and what you click

`setup\Setup-ChannelFlow.ps1` does everything Microsoft supports through an API. Every sign-in opens Microsoft Edge on this PC. The rest is five short manual steps.

| Step | Who | What |
|---|---|---|
| 1 | Script (`-Steps Team,Reference`) | Creates the private team **CGW Test - Governance queues** with five channels (you are owner) and a Teams **connection reference** in the Default solution |
| 2 | You, in Edge | Attach a Teams connection to the connection reference |
| 3 | Script (`-Steps Flow`) | Creates the flow from `channel-flow\flow-definition.json`, restricts the trigger to the Worker, turns it on |
| 4 | You, in Edge | Check the trigger setting, copy the trigger's HTTP URL |
| 5 | Script (`-Steps TriggerUrl`), then you | Paste the URL (hidden). Add a second co-owner (`-Steps CoOwner -CoOwnerUpn ...`) |

### Commands

```bash
pwsh -NoProfile -File setup/Setup-ChannelFlow.ps1 -TenantDomain cloudsconnected.com -AdminUpn joseph.saad@cloudsconnected.com -Steps Team,Reference
```

```bash
pwsh -NoProfile -File setup/Setup-ChannelFlow.ps1 -TenantDomain cloudsconnected.com -AdminUpn joseph.saad@cloudsconnected.com -Steps Flow
```

```bash
pwsh -NoProfile -File setup/Setup-ChannelFlow.ps1 -TenantDomain cloudsconnected.com -AdminUpn joseph.saad@cloudsconnected.com -Steps TriggerUrl
```

If the tenant has more than one Dataverse environment, add `-EnvironmentName "<name>"` or `-EnvironmentUrl https://<org>.crm.dynamics.com` to the first command. The script remembers it.

### Step 2: attach the Teams connection (once)

1. Edge opens **make.powerautomate.com > Solutions**. Pick the same environment the script printed (top right).
2. Open **Default Solution**, then **Connection references** on the left.
3. Open **CGW Test - Teams queues**.
4. Under **Connection**, choose your Microsoft Teams connection. If there is none, choose **+ New connection**, select **Microsoft Teams**, and sign in as joseph.saad@cloudsconnected.com.
5. Select **Save**.

### Step 4: check the trigger and copy its URL

1. In **Default Solution > Cloud flows**, open **CGW Test - Post governance card to queue channel** and select **Edit**.
2. Select the trigger **When an HTTP request is received**.
3. Check **Who can trigger the flow** = **Specific users in my tenant**. **Allowed users** must show the Worker's object ID (`workerServicePrincipalId` in `config\tenant.test.json`). If either is different, set it and **Save**. Microsoft documents this setting in the designer only, so the script's JSON values (`triggerAuthenticationType`, `triggerAllowedUsers`) need this one visual check.
4. Copy **HTTP URL** with the copy icon. Don't paste it anywhere except the script prompt. It carries a signature.
5. Run `-Steps TriggerUrl` and paste it. It is stored only in `config\appsettings.Local.json` (git-ignored) under `Notifications:ChannelFlowUrl`.

### Step 5: second co-owner

Flows belong to people and stop when their owner leaves (R20). Add a second real owner, for example the Platform Operations lead:

```bash
pwsh -NoProfile -File setup/Setup-ChannelFlow.ps1 -TenantDomain cloudsconnected.com -AdminUpn joseph.saad@cloudsconnected.com -Steps CoOwner -CoOwnerUpn someone@cloudsconnected.com
```

Or in Edge: open the flow, then **Share** (co-owners), and add the person.

## How the Worker calls the flow

| Setting | Value |
|---|---|
| URL | `Notifications:ChannelFlowUrl` from `config\appsettings.Local.json` |
| Method and type | `POST`, `Content-Type: application/json` |
| Token | App-only, Worker certificate (client credentials), scope `https://service.flow.microsoft.com//.default` (double slash, so the token's `aud` is `https://service.flow.microsoft.com/` exactly, with the trailing slash) |
| Header | `Authorization: Bearer <token>` |
| Body | `{ "queue": "INTAKE", "card": { ...Adaptive Card 1.4... }, "correlationId": "..." }` |
| Answers | `202` posted; `400` unknown queue; `401`/`403` token or caller not allowed |

The trigger checks the token's `aud`, `iss`, `tid` and `oid`. `oid` must be the Worker's service principal object ID. The Worker app needs no extra API permission: Entra issues the token without roles, and the trigger checks identity, not roles. The tenant must contain the Power Automate service principal, which it does wherever Power Automate has been used.

A sample body is in `channel-flow\sample-request.json`. The card's only action is a link back to the Workbench: nobody approves anything in Teams.

## Why this route

- **Dataverse Web API, not `api.flow.microsoft.com`.** Microsoft documents creating cloud flows through the `workflows` table (category 5) and says the `api.flow.microsoft.com` API is unsupported. The flow is therefore solution-aware in the Default solution, and its Teams connection goes through a connection reference.
- **Connections need a person.** A Teams connection is an OAuth consent by a user, so step 2 stays manual. It is done once.
- **Trigger URL.** Dataverse doesn't expose the trigger's callback URL, and the API that does is unsupported. So you copy it once from the designer.
- **Team and channels through Graph**, delegated as you (`Team.Create`, `Channel.Create`). You end up as owner.

## Known limits

- Microsoft notes that OAuth on HTTP triggers is still rolling out by region. If **Who can trigger the flow** is missing in the designer, the tenant doesn't have it yet. Leave the flow off and rely on email.
- Posting as **Flow bot** doesn't work in private channels. The five channels are standard channels in a private team.
- The flow posts with your Teams connection's license and limits. In production, use a service account that two co-owners can manage.
