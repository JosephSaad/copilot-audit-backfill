# Building the dashboard from source

How to compile the dashboard on a machine of your own, for a change made to its `.cs` files, and what that machine needs. Most installs never do this: a release carries the dashboard already built (`copilot-insights-vX.Y.Z.zip`), and `dashboard\Install-CopilotInsights.ps1` installs it with nothing but the Hosting Bundle on the server.

The installed site holds the compiled `CopilotInsights.dll`, not the source. A changed `.cs` file cannot be dropped into it: the whole project is built again and the site's files replaced.

## What the build needs

| Need | What | Where from |
|---|---|---|
| The .NET 10 SDK | Any 10.0 SDK, x64. The Hosting Bundle on a web server runs the dashboard and cannot build it. | https://dotnet.microsoft.com/download/dotnet/10.0, *SDK*, Windows x64 installer. The x64 zip under *Binaries* works unpacked to a folder, with no installer. |
| The source | The whole `dashboard\CopilotInsights` folder of the release, with your changed files in place. | The release's source zip, or the repository at the release's tag. |
| The NuGet packages | The packages `CopilotInsights.csproj` names, and what they bring in. | nuget.org, or your organization's own feed that mirrors it. |

Nothing else: no Node.js, no Visual Studio, no database. From 2.6.5 the release does not list or carry the packages: a restore from your feed brings them.

## Build and install

On the machine that builds, in PowerShell 7, from the folder that holds `dashboard`:

```powershell
dotnet publish dashboard\CopilotInsights\CopilotInsights.csproj -c Release -o out\CopilotInsights
```

The first build downloads the packages into `%USERPROFILE%\.nuget\packages`. To take them from your organization's feed instead of nuget.org, put a `nuget.config` beside `CopilotInsights.csproj` that clears the sources and names yours, or pass `--source <feed>` to a `dotnet restore` first and `--no-restore` to the publish.

Copy `out\CopilotInsights` to the web server and install it there, from an elevated window. The site's settings are kept:

```powershell
.\Install-CopilotInsights.ps1 -PackagePath .\out\CopilotInsights -SqlServer <server>
```

The web server then needs neither the SDK nor the packages. This is the route to prefer.

To build on the web server itself, install the SDK there and give the installer the source folder. It runs `dotnet publish` for you, which restores from the sources `nuget.config` names:

```powershell
.\Install-CopilotInsights.ps1 -PackagePath .\CopilotInsights -SqlServer <server>
```

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

- **Run the tests** when the build machine can: `dotnet test dashboard\CopilotInsights.Tests`.
- **The next release replaces your build.** Installing a release's zip over the site puts the released files back, so carry your change into the new source and build again, or send it to be included.
- **A changed build is yours to support.** Say which files you changed when you report a fault.
