# Sign-in through Microsoft Entra ID

From 2.8.0 the dashboard can sign people in through Microsoft Entra ID instead of Windows. Windows sign-in stays the
default, and nothing changes for a site that does not ask for Entra ID. The same app registration also lets the
dashboard check grants to **Entra groups** through Microsoft Graph, whichever way people sign in.

Who a person is, and what they may see, is settled exactly as before: their place in the org chart, their roles, the
minimum group size, privacy mode and the audit log. Only the way in changes.

## Contents

- [What you need](#what-you-need)
- [1. Register the app](#1-register-the-app)
- [2. Install or switch the site](#2-install-or-switch-the-site)
- [3. How a sign-in is matched to a person](#3-how-a-sign-in-is-matched-to-a-person)
- [4. Groups](#4-groups)
- [5. The data API](#5-the-data-api)
- [6. On Azure](#6-on-azure)
- [Settings](#settings)
- [Going back to Windows sign-in](#going-back-to-windows-sign-in)
- [What has not been tried](#what-has-not-been-tried)

## What you need

- An Entra ID administrator who can register an app and grant admin consent (an Application Administrator, with a
  Privileged Role Administrator or Global Administrator for the consent).
- **HTTPS** on the site: Entra ID returns people to an https:// address only.
- A **certificate** for the app, in Local Computer › Personal on every web server, with its private key. On Azure, a
  managed identity replaces it and no secret exists anywhere (section 6).
- The license tool (`tools\Update-CopilotAuditEnrichment.ps1`) run once with this release: it records each person's
  Entra object ID, which a sign-in is matched by first.

## 1. Register the app

In the Entra admin center, **App registrations › New registration**:

1. **Name** *Copilot Insights*; **Supported account types** *Accounts in this organizational directory only*.
2. **Redirect URI**: platform *Web*, `https://<site address>/signin-oidc`. Under **Authentication**, add
   `https://<site address>/signout-callback-oidc` as a second redirect URI. With a virtual directory, both include it:
   `https://apps.contoso.com/CopilotDashboard/signin-oidc`. Leave *Access tokens* and *ID tokens* (implicit grant) off:
   the dashboard uses the authorization code with PKCE.
3. **Certificates & secrets › Certificates › Upload certificate**: the public part (.cer) of the certificate installed
   on the web servers. Note its thumbprint.
4. **API permissions**:
   - *Microsoft Graph › Delegated › User.Read* (there by default): sign-in.
   - *Microsoft Graph › Application › GroupMember.Read.All*: which Entra groups a person is in, and the group names the
     grant form offers. Only needed for grants to Entra groups.
   - **Grant admin consent**.
5. **Token configuration › Add optional claim** (hybrid tenants): *ID* and *Access*, `onprem_sid`. It carries the
   person's on-premises SID, which matches them to the directory the nightly AD load builds, as Windows sign-in does.
6. **App roles › Create app role** (optional): display name *Copilot Insights administrator*, allowed member types
   *Users/Groups*, value `Insights.BootstrapAdmin`. Whoever holds it is a dashboard administrator, as
   `Insights:BootstrapAdmins` makes someone one, but granted and reviewed in Entra ID. Assign it in **Enterprise
   applications › Copilot Insights › Users and groups**.
7. **Expose an API** (for the data API only): set the Application ID URI to `api://<client ID>` and add a scope such as
   `Insights.Read`, admin consent only.
8. Optionally, in **Enterprise applications › Copilot Insights › Properties**, set *Assignment required* to *Yes* and
   assign the groups who may sign in at all. The dashboard's own rules apply after that either way.

Note the **Directory (tenant) ID** and **Application (client) ID** on the app's overview.

## 2. Install or switch the site

On each web server, with the certificate installed in Local Computer › Personal:

```powershell
.\Install-CopilotInsights.ps1 -PackagePath .\CopilotInsights -SqlServer sql01 -Authentication Entra `
    -EntraTenantId <tenant ID> -EntraClientId <client ID> -EntraCertificateThumbprint <thumbprint>
```

It writes the settings below into `appsettings.Production.json`, turns IIS **anonymous authentication on and Windows
authentication off** for the site (the site signs people in itself; every page still needs a signed-in person, and only
`/healthz` and the sign-in's own addresses answer without one), and gives the app-pool account read access to the
certificate's private key. A later run without `-Authentication` keeps the way the site has.

The header gains *Sign out*, which signs the person out of the dashboard and of Entra ID.

## 3. How a sign-in is matched to a person

In this order, the first that finds someone:

1. **The Entra object ID** (`oid` claim) against `dash.Person.ObjectId`, which the dashboard's refresh copies from the
   license tool's `audit.CopilotLicense.EntraObjectId`.
2. **The on-premises SID** (`onprem_sid` claim, if added in step 1.5) against `dash.Person.Sid`, as for a Windows sign-in.
3. **The UPN** the tenant gives the account (`upn`, else `preferred_username`), or an entry for it in
   `Insights:IdentityMap`, unless the directory records another account for that person: a different object ID, or,
   when the token carries one, a different on-premises SID. A UPN given to someone new never reaches the person who
   held it before.

Someone found none of these ways is "not in the directory" and sees nothing unless granted Organization, as with
Windows sign-in. Admin › Directory quality lists people with no SID; people with no object ID get one at the license
tool's next run.

## 4. Groups

- **Entra groups.** Grant a role or a profile to an Entra group in Admin › Access or Admin › Access profiles: the box
  *Or an Entra group* offers groups by name. The grant keeps the group's object ID (`entra:<ID>`), so renaming the
  group changes nothing. When the person signs in, the dashboard asks Microsoft Graph which of the granted groups they
  are in (`checkMemberGroups`, nested groups included), and keeps the answer ten minutes. It works with either sign-in;
  with Windows sign-in, the person is asked about by the object ID the license tool recorded. Graph being unreachable
  leaves Entra group grants out for a minute, logged: never more access than granted.
- **AD groups** (`DOMAIN\Group`) keep working. With Windows sign-in, from the Windows token, as before. With Entra
  sign-in there is no Windows token: the dashboard reads the members the nightly directory load records for every AD
  group named in a grant (`audit.GroupMember`), so a change to an AD group reaches the dashboard the next morning.
- **Access profiles** (Admin › Access profiles) bundle roles; grant them to groups, and send requests through an Entra
  access package for each ([OPERATIONS.md](OPERATIONS.md#access-profiles)).

## 5. The data API

With Entra sign-in, `/api/v1/...` takes a bearer token issued for the app's API (`api://<client ID>`, or the client ID
itself), for a person: the same rules as their pages then apply. A browser already signed in to the dashboard reaches
the API with its cookie. A token for an application, with no person in it, is no one in the directory and gets nothing.

## 6. On Azure

The same settings, with Azure in place of the certificate and the shared folder:

- **No secret.** Set `Insights:Entra:ClientCredential` to `ManagedIdentity` (and `ManagedIdentityClientId` for a
  user-assigned identity). On the app registration, add a **federated credential** for that managed identity
  (Certificates & secrets › Federated credentials › *Managed Identity*). Give the managed identity itself the Graph
  application permission GroupMember.Read.All for the group checks.
- **Form keys** in one place every web server reads: `Insights:DataProtection` = `Blob`,
  `Insights:DataProtectionBlobUri` (a blob such as `https://<account>.blob.core.windows.net/keys/keys.xml`) and
  `Insights:DataProtectionKeyVaultKeyId` (a key such as `https://<vault>.vault.azure.net/keys/dataprotection`). The
  identity needs *Storage Blob Data Contributor* on the container and *Key Vault Crypto Service Encryption User* on the
  key. The site does not start if they are set wrong, with the reason in its log.
- **Application Insights.** `Insights:ApplicationInsightsConnectionString`: requests, failures and the site's
  warnings go there as well as to the log. Empty: nothing is sent anywhere.

## Settings

`appsettings.Production.json`, under `Insights`:

```json
{
  "Insights": {
    "Authentication": "Entra",
    "Entra": {
      "TenantId": "<tenant ID>",
      "ClientId": "<client ID>",
      "ClientCredential": "Certificate",
      "CertificateThumbprint": "<thumbprint>",
      "BootstrapAdminRole": "Insights.BootstrapAdmin"
    }
  }
}
```

| Setting | Default | What it is |
|---|---|---|
| `Authentication` | `Windows` | `Windows` or `Entra` |
| `Entra:Instance` | `https://login.microsoftonline.com/` | The cloud's sign-in address |
| `Entra:TenantId`, `Entra:ClientId` | none | The app registration |
| `Entra:ClientCredential` | `Certificate` | `Certificate`, `ManagedIdentity` or `Secret` |
| `Entra:CertificateThumbprint` | none | With `Certificate`: in Local Computer › Personal |
| `Entra:ManagedIdentityClientId` | none | With `ManagedIdentity`: a user-assigned identity; empty for the system one |
| `Entra:ClientSecret` | none | With `Secret`, the last resort: set it as the environment variable `Insights__Entra__ClientSecret`, never in a file |
| `Entra:CallbackPath` | `/signin-oidc` | Where Entra ID returns people |
| `Entra:ApiAudience` | `api://<client ID>` | The data API's audience |
| `Entra:GraphUrl` | `https://graph.microsoft.com` | Microsoft Graph in this cloud |
| `Entra:BootstrapAdminRole` | `Insights.BootstrapAdmin` | The app role that makes administrators |
| `DataProtection`, `DataProtectionBlobUri`, `DataProtectionKeyVaultKeyId` | none | Form keys on Azure (section 6) |
| `ApplicationInsightsConnectionString` | none | Application Insights (section 6) |

`Insights:BootstrapAdmins` keeps working with Entra sign-in for UPNs; a `DOMAIN\Group` there needs Windows sign-in.

## Going back to Windows sign-in

Run the installer with `-Authentication Windows`: IIS Windows authentication is turned back on and anonymous off. The
`Entra` settings are kept, so grants to Entra groups keep being checked through Graph.

## What has not been tried

Built and tested against stand-ins in 2.8.0, not against a real tenant: a real sign-in, the federated credential of a
managed identity, Graph's answers for a real group, Key Vault and Blob Storage, and Application Insights. Try it on a
test site first, with a bootstrap administrator who can sign in with Windows on the production one.
