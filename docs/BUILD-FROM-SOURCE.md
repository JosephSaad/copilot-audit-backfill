# Building the dashboard from source

How to compile the dashboard on a machine of your own, for a change made to its `.cs` files, and what that machine needs. Most installs never do this: a release carries the dashboard already built (`copilot-insights-vX.Y.Z.zip`), and `dashboard\Install-CopilotInsights.ps1` installs it with nothing but the Hosting Bundle on the server.

The installed site holds the compiled `CopilotInsights.dll`, not the source. A changed `.cs` file cannot be dropped into it: the whole project is built again and the site's files replaced.

## What the build needs

| Need | What | Where from |
|---|---|---|
| The .NET 10 SDK | Any 10.0 SDK, x64; 2.6.4 was built and tested with 10.0.401. The Hosting Bundle on a web server runs the dashboard and cannot build it. | https://dotnet.microsoft.com/download/dotnet/10.0, *SDK*, Windows x64 installer. The x64 zip under *Binaries* works unpacked to a folder, with no installer. |
| The source | The whole `dashboard\CopilotInsights` folder of the release, with your changed files in place. | The release's source zip, or the repository at the release's tag. |
| The NuGet packages | The 24 packages below, 36 MB to download. | nuget.org, or a folder copied from a machine that has them (see [With no internet access](#with-no-internet-access)). |

Nothing else: no Node.js, no Visual Studio, no database. The tests (`dashboard\CopilotInsights.Tests`) need three more packages, listed at the end, and are not needed to build.

## The NuGet packages

The project names five packages (`CopilotInsights.csproj`). They bring in nineteen more. A restore needs all 24, at these versions.

<!-- packages:start -->
| Package | Version | Needed by |
|---|---|---|
| `BouncyCastle.Cryptography` | 2.7.0 | MimeKit |
| `Dapper` | 2.1.89 | the project |
| `MailKit` | 4.18.0 | the project |
| `Microsoft.AspNetCore.Authentication.Negotiate` | 10.0.12 | the project |
| `Microsoft.Bcl.Cryptography` | 9.0.18 | Microsoft.Data.SqlClient |
| `Microsoft.Data.SqlClient` | 7.1.0 | the project |
| `Microsoft.Data.SqlClient.Extensions.Abstractions` | 7.1.0 | Microsoft.Data.SqlClient |
| `Microsoft.Data.SqlClient.Internal.Logging` | 7.1.0 | Microsoft.Data.SqlClient, Microsoft.Data.SqlClient.Extensions.Abstractions |
| `Microsoft.Data.SqlClient.SNI.runtime` | 7.1.0 | Microsoft.Data.SqlClient |
| `Microsoft.IdentityModel.Abstractions` | 8.16.0 | Microsoft.IdentityModel.Logging |
| `Microsoft.IdentityModel.JsonWebTokens` | 8.16.0 | Microsoft.Data.SqlClient, System.IdentityModel.Tokens.Jwt |
| `Microsoft.IdentityModel.Logging` | 8.16.0 | Microsoft.IdentityModel.Tokens |
| `Microsoft.IdentityModel.Protocols` | 8.16.0 | Microsoft.IdentityModel.Protocols.OpenIdConnect |
| `Microsoft.IdentityModel.Protocols.OpenIdConnect` | 8.16.0 | Microsoft.Data.SqlClient |
| `Microsoft.IdentityModel.Tokens` | 8.16.0 | Microsoft.IdentityModel.JsonWebTokens, Microsoft.IdentityModel.Protocols, System.IdentityModel.Tokens.Jwt |
| `Microsoft.SqlServer.Server` | 1.0.0 | Microsoft.Data.SqlClient |
| `MimeKit` | 4.18.0 | MailKit |
| `System.Configuration.ConfigurationManager` | 10.0.12 | Microsoft.Data.SqlClient, System.DirectoryServices.AccountManagement |
| `System.DirectoryServices` | 10.0.12 | System.DirectoryServices.AccountManagement |
| `System.DirectoryServices.AccountManagement` | 10.0.12 | the project |
| `System.DirectoryServices.Protocols` | 10.0.12 | Microsoft.AspNetCore.Authentication.Negotiate, System.DirectoryServices.AccountManagement |
| `System.IdentityModel.Tokens.Jwt` | 8.16.0 | Microsoft.IdentityModel.Protocols.OpenIdConnect |
| `System.Security.Cryptography.Pkcs` | 10.0.0 | Microsoft.Data.SqlClient, MimeKit |
| `System.Security.Cryptography.ProtectedData` | 10.0.12 | System.Configuration.ConfigurationManager |
<!-- packages:end -->

What each of the five is for:

| Package | Use in the dashboard |
|---|---|
| `Dapper` | Reads query results into the pages' rows. |
| `MailKit` | Sends the digests, alerts and extracts by SMTP. |
| `Microsoft.AspNetCore.Authentication.Negotiate` | Windows sign-in (Kerberos and NTLM). |
| `Microsoft.Data.SqlClient` | The connection to SQL Server. |
| `System.DirectoryServices.AccountManagement` | Looks a sign-in's UPN up in Active Directory, where the settings ask for it. |

A test keeps this list true (`BuildFromSourceTests`): it fails when the packages a restore brings are not the ones listed here, so a release that moves a version updates the table.

## Build and install, with internet access

On the machine that builds, in PowerShell 7, from the folder that holds `dashboard`:

```powershell
dotnet publish dashboard\CopilotInsights\CopilotInsights.csproj -c Release -o out\CopilotInsights
```

The first build downloads the packages from nuget.org into `%USERPROFILE%\.nuget\packages`. Copy `out\CopilotInsights` to the web server and install it there, from an elevated window. The site's settings are kept:

```powershell
.\Install-CopilotInsights.ps1 -PackagePath .\out\CopilotInsights -SqlServer <server>
```

The web server then needs neither the SDK nor the packages. This is the route to prefer.

To build on the web server itself, install the SDK there and give the installer the source folder. It runs `dotnet publish` for you:

```powershell
.\Install-CopilotInsights.ps1 -PackagePath .\CopilotInsights -SqlServer <server>
```

## With no internet access

A machine that cannot reach nuget.org restores from a folder of the packages.

1. **On a machine with internet access** and the SDK, fill a folder with the 24 packages:

   ```powershell
   dotnet restore dashboard\CopilotInsights\CopilotInsights.csproj --packages C:\insights-packages
   ```

   The folder is about 200 MB, one subfolder a package.

2. **Copy `C:\insights-packages`** to the machine that builds, with the source and the SDK's installer.

3. **On the machine that builds,** restore from the folder and nothing else:

   ```powershell
   dotnet restore dashboard\CopilotInsights\CopilotInsights.csproj --source C:\insights-packages
   ```

4. **Publish, or install from source,** as above. With the packages restored, neither reaches for the network.

An organization with its own package feed (Azure Artifacts, Nexus, Artifactory) can name it with `--source` in step 3 instead, once the 24 packages are in it.

## An example: one more filter on Agent inventory

Agents › Agent inventory filters on columns of the agent workbook's `AgentInventory` sheet, as the nightly load keeps it in `dbo.AgentInventory`. Each filter is one line in `Pages\OperationsReports.cs`, in `InventoryModel`: its name in the address, the label of its box, and the sheet's column heading.

```csharp
protected override IReadOnlyList<WorkbookFilter> Filters =>
[
    new("createdin", "Created in", "CreatedIn"),
    new("env", "Environment type", "EnvironmentSku"),   // added
];
```

Nothing else changes: the box lists the values the column holds, the choice is kept as the period and the part of the organization change, and a column the loaded workbook lacks is simply not offered. Build and install as above.

## After a change

- **Run the tests** when the build machine can: `dotnet test dashboard\CopilotInsights.Tests`. They need three more packages, and what those bring: `Microsoft.NET.Test.Sdk` 17.14.1, `xunit` 2.9.3 and `xunit.runner.visualstudio` 3.1.4.
- **The next release replaces your build.** Installing a release's zip over the site puts the released files back, so carry your change into the new source and build again, or send it to be included.
- **A changed build is yours to support.** Say which files you changed when you report a fault.
