<#
.SYNOPSIS
    Builds the Workbench Teams app package (manifest, color and outline icons) as a zip.

.DESCRIPTION
    The app carries the activity feed types the Worker sends and one personal tab that hosts the
    Workbench launcher page. webApplicationInfo names the Worker app, which sends the notifications
    with the TeamsActivity.Send.User resource-specific consent granted at install.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $WorkerClientId,
    [Parameter(Mandatory)] [string] $WebBaseUrl,
    [Parameter(Mandatory)] [string] $OutFile,
    [string] $Suffix = 'Test',
    [string] $Version = '1.0.0',
    [string] $TemplateDir = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$work = Join-Path ([IO.Path]::GetTempPath()) ("cgw-teams-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    Add-Type -AssemblyName System.Drawing
    function New-Icon([int] $size, [string] $path, [bool] $outline) {
        $bmp = [System.Drawing.Bitmap]::new($size, $size)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::Transparent)
        if ($outline) {
            $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [Math]::Max(2, $size / 12))
            $g.DrawEllipse($pen, $size * 0.12, $size * 0.12, $size * 0.76, $size * 0.76)
            $g.DrawLine($pen, $size * 0.32, $size * 0.52, $size * 0.46, $size * 0.66)
            $g.DrawLine($pen, $size * 0.46, $size * 0.66, $size * 0.70, $size * 0.36)
        }
        else {
            $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 37, 99, 235))
            $g.FillRectangle($brush, 0, 0, $size, $size)
            $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $size / 14)
            $g.DrawEllipse($pen, $size * 0.2, $size * 0.2, $size * 0.6, $size * 0.6)
            $g.DrawLine($pen, $size * 0.36, $size * 0.52, $size * 0.47, $size * 0.63)
            $g.DrawLine($pen, $size * 0.47, $size * 0.63, $size * 0.66, $size * 0.40)
        }
        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
    }
    New-Icon 192 (Join-Path $work 'color.png') $false
    New-Icon 32 (Join-Path $work 'outline.png') $true

    # Teams requires https for tab content. In the local test the tab cannot load (the site is
    # http on localhost); activity feed notifications still work and deep link to the app.
    $httpsBase = $WebBaseUrl -replace '^http://', 'https://'
    $host_ = ([uri]$httpsBase).Host

    $activityTypes = @(
        @{ type = 'taskAssigned'; description = 'A task was assigned to you'; templateText = '{actor} assigned you {taskTitle}' },
        @{ type = 'requestReturned'; description = 'Your request was returned'; templateText = 'Request {requestNumber} was returned to you' },
        @{ type = 'decisionRecorded'; description = 'A decision was recorded'; templateText = 'Decision on {requestNumber}: {outcome}' },
        @{ type = 'verificationFailed'; description = 'Verification failed'; templateText = 'Verification failed on {requestNumber}' },
        @{ type = 'recertificationDue'; description = 'Recertification due'; templateText = 'Recertification due for {workloadName}' }
    )

    $manifest = [ordered]@{
        '$schema'           = 'https://developer.microsoft.com/json-schemas/teams/v1.19/MicrosoftTeams.schema.json'
        manifestVersion     = '1.19'
        version             = $Version
        id                  = $WorkerClientId
        developer           = [ordered]@{
            name          = 'Clouds Connected'
            websiteUrl    = $httpsBase
            privacyUrl    = "$httpsBase/Privacy"
            termsOfUseUrl = "$httpsBase/Terms"
        }
        name                = [ordered]@{ short = "Governance Workbench ($Suffix)"; full = "Copilot Governance Workbench ($Suffix)" }
        description         = [ordered]@{
            short = 'Tasks and decisions for Copilot consumption governance.'
            full  = 'Notices from the Copilot Governance Workbench: tasks assigned to you, returned requests, decisions and recertification. Every decision happens in the Workbench, never in Teams.'
        }
        icons               = [ordered]@{ color = 'color.png'; outline = 'outline.png' }
        accentColor         = '#2563EB'
        staticTabs          = @([ordered]@{
                entityId   = 'launch'
                name       = 'Workbench'
                contentUrl = "$httpsBase/Teams/Launch?item={subEntityId}"
                websiteUrl = "$httpsBase/Start"
                scopes     = @('personal')
            })
        permissions         = @('identity')
        validDomains        = @($host_)
        webApplicationInfo  = [ordered]@{ id = $WorkerClientId; resource = "api://$WorkerClientId" }
        authorization       = [ordered]@{
            permissions = [ordered]@{
                resourceSpecific = @([ordered]@{ name = 'TeamsActivity.Send.User'; type = 'Application' })
            }
        }
        activities          = [ordered]@{ activityTypes = $activityTypes }
    }
    $manifest | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $work 'manifest.json') -Encoding utf8NoBOM
    if ($TemplateDir) {
        New-Item -ItemType Directory -Force $TemplateDir | Out-Null
        Copy-Item (Join-Path $work '*') $TemplateDir -Force
    }
    New-Item -ItemType Directory -Force (Split-Path $OutFile) | Out-Null
    if (Test-Path $OutFile) { [IO.File]::Delete($OutFile) }
    Compress-Archive -Path (Join-Path $work '*') -DestinationPath $OutFile
    Write-Host "   ok  Teams package $OutFile" -ForegroundColor Green
}
finally {
    [IO.Directory]::Delete($work, $true)
}
