#Requires -Version 5.1
<#
.SYNOPSIS
    The weekly Copilot report: a table and two charts by email, with an Excel workbook.

.DESCRIPTION
    Reads one Saturday-to-Friday week from the Copilot database
    (bi.usp_WeeklyCopilotReport, sql\Create-WeeklyReport.sql) and sends:

      - the table: Users enabled, Active users, Actions taken (prompts) and
        Security alerts, for M365 Copilot (use with a Copilot license) and
        Copilot Chat (use without one), for the week and for all time;
      - two charts over the weeks before it (13 by default, about 90 days):
        Copilot Chat and M365 Copilot users enabled, active users and
        actions taken;
      - an Excel workbook with the same table, the weeks' figures and the two
        charts as native Excel charts that can be edited.

    With -Scheduled (how the task runs it, every hour) it sends each week once,
    on the first run after the send day and time (Tuesday 08:00 by default) in
    the reporting time zone. Before sending it waits up to MaxWaitHours for the
    data to reach the Monday after the week, so records Purview delivers late
    are in. A week already sent is not sent again; a failed send is tried again
    on the next run, up to MaxAttempts. Every send and failure is recorded in
    audit.WeeklyReportSend.
    In their words (3.1.6): the success stories a curator ticked on the
    dashboard's Success stories page, approved, up to StoriesPerReport (3 by
    default, oldest approved first), are quoted in the email and listed in the
    workbook's Stories sheet. A sent report stamps each with the week and
    clears its tick, so a story goes out once unless ticked again. A preview
    or a send with -To quotes them and stamps nothing. On the day before the
    send day, from the send time, the curators named in StoriesReminderTo
    (dashboard setting) get one reminder a week when stories wait for review
    or none is ticked.

    It sends through the mail server the dashboard uses (3.1.3): the way of
    sending, server, port, security, user name, password, certificate check,
    From and Reply-To set in Admin > Email, read from the Copilot database
    (dash.Setting, and the password through dash.usp_GetSecret). Nothing about
    the mail server is in this script's settings or stored on the job server.

    Runs in Windows PowerShell 5.1 (powershell.exe), not PowerShell 7: the
    charts are drawn with the .NET Framework's chart library. The workbook uses
    ImportExcel (the release's Modules folder holds it); without it the email
    goes without the workbook.

.PARAMETER SettingsPath
    The report's settings. Default: CopilotWeeklyReport.settings.psd1 in the
    folder above this script (where copilot-audit.settings.psd1 is).

.PARAMETER WeekEnding
    The Friday that ends the week to report. Default: the last complete week.

.PARAMETER Scheduled
    Send only when due and not yet sent (the scheduled task passes it).

.PARAMETER Preview
    Write the email (HTML), the charts and the workbook to -OutputFolder and
    send nothing. Nothing is recorded.

.PARAMETER To
    Send to these addresses instead of the settings' To, Cc and Bcc. For a test;
    nothing is recorded, so the scheduled send still goes.

.PARAMETER Force
    Send even though the week was already sent. Recorded as a send.

.PARAMETER OutputFolder
    Where -Preview writes. Default: reports\preview under the scripts' folder.

.PARAMETER IncludeDisabled
    For this run only, count disabled accounts and people who have left in the
    two people rows: Users Enabled counts every member of the AD groups, and
    Active Users everyone who used Copilot (3.1.3). Without it, the run follows
    the dashboard's setting (Admin > Alerts and digests > Weekly Copilot
    report), which the scheduled task uses. Actions taken and Security Alerts
    count all use either way.

.EXAMPLE
    powershell.exe -NoProfile -File .\tools\Send-CopilotWeeklyReport.ps1 -Preview

.EXAMPLE
    powershell.exe -NoProfile -File .\tools\Send-CopilotWeeklyReport.ps1 -To me@contoso.com

.EXAMPLE
    powershell.exe -NoProfile -File .\tools\Send-CopilotWeeklyReport.ps1 -Preview -IncludeDisabled
#>
[CmdletBinding()]
param(
    [string]   $SettingsPath,
    [datetime] $WeekEnding,
    [switch]   $Scheduled,
    [switch]   $Preview,
    [string[]] $To,
    [switch]   $Force,
    [string]   $OutputFolder,
    [switch]   $IncludeDisabled
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$script:Root = Split-Path $PSScriptRoot -Parent
$script:EnDash = [string] [char] 0x2013
$script:LogPath = $null

$modules = Join-Path $script:Root 'Modules'
if (Test-Path -LiteralPath $modules) { $env:PSModulePath = $modules + [IO.Path]::PathSeparator + $env:PSModulePath }

# ===========================================================================
# Settings and log
# ===========================================================================

function Write-ReportLog {
    param([string] $Message, [string] $Level = 'INFO')
    $line = '{0:yyyy-MM-dd HH:mm:ss} {1,-5} {2}' -f (Get-Date), $Level, $Message
    if ($Level -eq 'ERROR') { Write-Warning $Message } else { Write-Host $line }
    if ($script:LogPath) {
        try { Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8 } catch { Write-Verbose "Log not written: $($_.Exception.Message)" }
    }
}

function Get-ReportSetting {
    param([string] $Path)
    if (-not $Path) { $Path = Join-Path $script:Root 'CopilotWeeklyReport.settings.psd1' }
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "No settings file at $Path. Copy CopilotWeeklyReport.settings.example.psd1 there and fill it in."
    }
    $s = Import-PowerShellDataFile -LiteralPath $Path
    $d = @{
        SqlServer = ''; Database = ''; TrustServerCertificate = $null; ConnectionString = ''
        To = @(); Cc = @(); Bcc = @(); SubjectPrefix = 'Copilot weekly report'
        SendDay = 'Tuesday'; SendTime = '08:00'; MaxWaitHours = 24; MaxAttempts = 8
        Weeks = 13; KeepCopies = $true
    }
    foreach ($k in @($s.Keys)) { $d[$k] = $s[$k] }
    # 3.1.3: the mail server and From come from the dashboard's Admin > Email; 3.1.2's own settings for them are not read.
    # ChatEnabled likewise: Users Enabled comes from the AD groups the nightly load counts.
    $script:UnusedMailSettings = @(@('SmtpServer', 'SmtpPort', 'SmtpUseSsl', 'SmtpUseDefaultCredentials', 'From', 'ChatEnabled') | Where-Object { $s.Contains($_) })

    # The database comes from the pipeline's own settings when this file leaves it out.
    $main = Join-Path $script:Root 'copilot-audit.settings.psd1'
    if ((-not $d.SqlServer -and -not $d.ConnectionString) -and (Test-Path -LiteralPath $main)) {
        $m = Import-PowerShellDataFile -LiteralPath $main
        if ($m.Contains('SqlServer')) { $d.SqlServer = $m.SqlServer }
        if (-not $d.Database -and $m.Contains('Database')) { $d.Database = $m.Database }
        if ($null -eq $d.TrustServerCertificate -and $m.Contains('TrustServerCertificate')) { $d.TrustServerCertificate = $m.TrustServerCertificate }
        if ($m.Contains('ConnectionString') -and $m.ConnectionString) { $d.ConnectionString = $m.ConnectionString }
    }
    if (-not $d.Database) { $d.Database = 'CopilotAudit' }
    if (-not $d.SqlServer -and -not $d.ConnectionString) { throw 'No SqlServer in the settings, and none in copilot-audit.settings.psd1.' }
    $null = [DayOfWeek] $d.SendDay
    $null = [TimeSpan]::Parse($d.SendTime)
    $d
}

# ===========================================================================
# SQL
# ===========================================================================

function Open-ReportConnection {
    param([hashtable] $Setting)
    if ($Setting.ConnectionString) { $cs = [string] $Setting.ConnectionString }
    else {
        $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
        $b['Data Source'] = $Setting.SqlServer
        $b['Initial Catalog'] = $Setting.Database
        $b['Integrated Security'] = $true
        $b['Encrypt'] = $true
        $b['TrustServerCertificate'] = [bool] $Setting.TrustServerCertificate
        $cs = $b.ConnectionString
    }
    $b2 = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($cs)
    $b2['Application Name'] = 'Copilot weekly report'
    $c = New-Object System.Data.SqlClient.SqlConnection($b2.ConnectionString)
    $c.Open()
    $c
}

function Invoke-ReportQuery {
    param($Connection, [string] $Sql, [hashtable] $Parameters = @{}, [int] $Timeout = 600)
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $Sql
    $cmd.CommandTimeout = $Timeout
    foreach ($k in $Parameters.Keys) {
        $v = $Parameters[$k]
        if ($null -eq $v) { $v = [DBNull]::Value }
        [void] $cmd.Parameters.AddWithValue($k, $v)
    }
    $ds = New-Object System.Data.DataSet
    $da = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
    [void] $da.Fill($ds)
    $ds
}

function Get-SendRecord {
    param($Connection, [datetime] $Week)
    $ds = Invoke-ReportQuery $Connection 'SELECT Status, Attempts, SentUtc FROM audit.WeeklyReportSend WHERE WeekEnding = @w;' @{ '@w' = $Week.Date }
    if ($ds.Tables[0].Rows.Count) { $ds.Tables[0].Rows[0] } else { $null }
}

function Set-SendRecord {
    param($Connection, [datetime] $Week, [string] $Status, [string] $Recipients, $DataComplete, [string] $Detail)
    $sql = @'
MERGE audit.WeeklyReportSend AS t
USING (SELECT @w AS WeekEnding) AS s ON t.WeekEnding = s.WeekEnding
WHEN MATCHED THEN UPDATE SET
    Status = @st, Attempts = t.Attempts + 1, LastTryUtc = SYSUTCDATETIME(),
    SentUtc = CASE WHEN @st = 'Sent' THEN SYSUTCDATETIME() ELSE t.SentUtc END,
    Recipients = @r, DataComplete = @dc, Detail = @d
WHEN NOT MATCHED THEN INSERT (WeekEnding, Status, Attempts, SentUtc, Recipients, DataComplete, Detail)
    VALUES (@w, @st, 1, CASE WHEN @st = 'Sent' THEN SYSUTCDATETIME() END, @r, @dc, @d);
'@
    if ($Detail -and $Detail.Length -gt 4000) { $Detail = $Detail.Substring(0, 4000) }
    if ($Recipients -and $Recipients.Length -gt 4000) { $Recipients = $Recipients.Substring(0, 4000) }
    [void] (Invoke-ReportQuery $Connection $sql @{ '@w' = $Week.Date; '@st' = $Status; '@r' = $Recipients; '@dc' = $DataComplete; '@d' = $Detail })
}

# ===========================================================================
# Formatting
# ===========================================================================

function Format-Count {
    param($Value)
    if ($null -eq $Value -or $Value -is [DBNull]) { return [string] $script:EnDash }
    ([long] $Value).ToString('#,##0', [Globalization.CultureInfo]::InvariantCulture)
}

function ConvertTo-Html5Text {
    param([string] $Text)
    [System.Net.WebUtility]::HtmlEncode($Text)
}

$script:Palette = @{
    Navy      = '#1F3B5A'   # enabled line, headings
    Orange    = '#E8743B'   # active line
    AreaFill  = '#BCCFE0'   # actions area
    AreaEdge  = '#8FAAC4'
    HeadDark  = '#1F5C7A'   # table header
    SubHead   = '#BDD7EE'
    RowA      = '#DDEBF7'
    RowB      = '#CFE2F3'
    Label     = '#24607F'
    Grid      = '#E3E8EE'
    Text      = '#1B1B1B'
    Muted     = '#5F6B76'
}

# ===========================================================================
# Charts (PNG, for the email)
# ===========================================================================

function Get-NiceMax {
    <# A round axis maximum at or above Value: 1, 2, 2.5 or 5 times a power of ten. #>
    param([double] $Value)
    if ($Value -le 0) { return 10 }
    $p = [math]::Pow(10, [math]::Floor([math]::Log10($Value)))
    foreach ($m in 1, 2, 2.5, 5, 10) { if ($m * $p -ge $Value) { return $m * $p } }
    10 * $p
}

function New-WeeklyChartPng {
    <#
        One chart: users enabled and active users as lines on the left axis,
        actions taken as an area on the right axis, one point per week.
        Drawn at twice the size it is shown at, so it stays sharp.
    #>
    param(
        [string] $Title, [System.Data.DataTable] $Weeks, [string] $Prefix,
        [string] $EnabledName, [string] $ActiveName, [string] $ActionsName, [string] $Path
    )
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Windows.Forms.DataVisualization
    $ns = 'System.Windows.Forms.DataVisualization.Charting'
    $k = 2
    $font = { param([float] $size, [Drawing.FontStyle] $style = 'Regular') New-Object Drawing.Font('Segoe UI', ($size * $k), $style, [Drawing.GraphicsUnit]::Pixel) }
    $col = { param([string] $hex, [int] $alpha = 255) $c = [Drawing.ColorTranslator]::FromHtml($hex); [Drawing.Color]::FromArgb($alpha, $c) }

    $chart = New-Object "$ns.Chart"
    $chart.Width = 900 * $k
    $chart.Height = 380 * $k
    $chart.BackColor = [Drawing.Color]::White
    $chart.AntiAliasing = [System.Windows.Forms.DataVisualization.Charting.AntiAliasingStyles]::All
    $chart.TextAntiAliasingQuality = [System.Windows.Forms.DataVisualization.Charting.TextAntiAliasingQuality]::High

    $t = New-Object "$ns.Title"
    $t.Text = $Title
    $t.Font = & $font 17 ([Drawing.FontStyle]::Bold)
    $t.ForeColor = & $col $script:Palette.Navy
    $t.Alignment = [Drawing.ContentAlignment]::TopLeft
    [void] $chart.Titles.Add($t)

    $area = New-Object "$ns.ChartArea" 'main'
    $area.BackColor = [Drawing.Color]::White
    $area.Position.Auto = $false
    $area.Position.X = 1; $area.Position.Y = 9; $area.Position.Width = 98; $area.Position.Height = 78

    $enabledMax = 0.0; $activeMax = 0.0; $actionsMax = 0.0
    foreach ($r in $Weeks.Rows) {
        if (-not ($r["${Prefix}Enabled"] -is [DBNull])) { $enabledMax = [math]::Max($enabledMax, [double] $r["${Prefix}Enabled"]) }
        $activeMax = [math]::Max($activeMax, [double] $r["${Prefix}Active"])
        $actionsMax = [math]::Max($actionsMax, [double] $r["${Prefix}Actions"])
    }
    # Lines in the upper part, the area in the lower part, so their labels stay apart.
    $yMax = Get-NiceMax ([math]::Max($enabledMax, $activeMax) * 1.12)
    $y2Max = Get-NiceMax ($actionsMax * 1.55)

    foreach ($ax in $area.AxisY, $area.AxisY2) {
        $ax.Minimum = 0
        $ax.LabelStyle.Format = '#,##0'
        $ax.LabelStyle.Font = & $font 11
        $ax.LabelStyle.ForeColor = & $col $script:Palette.Muted
        $ax.LineColor = [Drawing.Color]::Transparent
        $ax.MajorTickMark.Enabled = $false
        $ax.IsLabelAutoFit = $false
    }
    $area.AxisY.Maximum = $yMax
    $area.AxisY.Interval = $yMax / 5
    $area.AxisY.MajorGrid.LineColor = & $col $script:Palette.Grid
    $area.AxisY.MajorGrid.LineDashStyle = [System.Windows.Forms.DataVisualization.Charting.ChartDashStyle]::Solid
    $area.AxisY.Title = 'Users'
    $area.AxisY.TitleFont = & $font 11
    $area.AxisY.TitleForeColor = & $col $script:Palette.Muted
    $area.AxisY2.Enabled = [System.Windows.Forms.DataVisualization.Charting.AxisEnabled]::True
    $area.AxisY2.Maximum = $y2Max
    $area.AxisY2.Interval = $y2Max / 5
    $area.AxisY2.MajorGrid.Enabled = $false
    $area.AxisY2.Title = 'Actions taken'
    $area.AxisY2.TitleFont = & $font 11
    $area.AxisY2.TitleForeColor = & $col $script:Palette.Muted

    $area.AxisX.Interval = 1
    $area.AxisX.MajorGrid.Enabled = $false
    $area.AxisX.MajorTickMark.Enabled = $false
    $area.AxisX.LineColor = & $col '#B8C2CC'
    $area.AxisX.LabelStyle.Font = & $font 10.5
    $area.AxisX.LabelStyle.ForeColor = & $col $script:Palette.Muted
    $area.AxisX.IsLabelAutoFit = $false
    $area.AxisX.IsMarginVisible = $true
    $area.AxisX.Title = 'Week of (Saturday)'
    $area.AxisX.TitleFont = & $font 11
    $area.AxisX.TitleForeColor = & $col $script:Palette.Muted
    [void] $chart.ChartAreas.Add($area)

    $legend = New-Object "$ns.Legend"
    $legend.Docking = [System.Windows.Forms.DataVisualization.Charting.Docking]::Bottom
    $legend.Alignment = [Drawing.StringAlignment]::Center
    $legend.Font = & $font 12
    $legend.ForeColor = & $col $script:Palette.Text
    $legend.IsTextAutoFit = $false
    [void] $chart.Legends.Add($legend)

    $sArea = New-Object "$ns.Series" $ActionsName
    $sArea.ChartType = [System.Windows.Forms.DataVisualization.Charting.SeriesChartType]::Area
    $sArea.YAxisType = [System.Windows.Forms.DataVisualization.Charting.AxisType]::Secondary
    $sArea.Color = & $col $script:Palette.AreaFill 190
    $sArea.BorderColor = & $col $script:Palette.AreaEdge
    $sArea.BorderWidth = 2 * $k
    $sArea.IsValueShownAsLabel = $true
    $sArea.LabelFormat = '#,##0'
    $sArea.Font = & $font 10.5
    $sArea.LabelForeColor = & $col '#46586A'

    $sEnabled = New-Object "$ns.Series" $EnabledName
    $sActive = New-Object "$ns.Series" $ActiveName
    foreach ($pair in @(@($sEnabled, $script:Palette.Navy, 'Top'), @($sActive, $script:Palette.Orange, 'Bottom'))) {
        $s = $pair[0]
        $s.ChartType = [System.Windows.Forms.DataVisualization.Charting.SeriesChartType]::Line
        $s.Color = & $col $pair[1]
        $s.BorderWidth = 3 * $k
        $s.MarkerStyle = [System.Windows.Forms.DataVisualization.Charting.MarkerStyle]::Circle
        $s.MarkerSize = 7 * $k
        $s.MarkerColor = [Drawing.Color]::White
        $s.MarkerBorderColor = & $col $pair[1]
        $s.MarkerBorderWidth = 2 * $k
        $s.IsValueShownAsLabel = $true
        $s.LabelFormat = '#,##0'
        $s.Font = & $font 10.5 ([Drawing.FontStyle]::Bold)
        $s.LabelForeColor = & $col $pair[1]
        $s['LabelStyle'] = $pair[2]
    }

    # The values go in as objects: with a number as the second argument, Windows
    # PowerShell picks AddXY(double, double[]) and cannot turn the label "7/25"
    # into the X number. As an object the label becomes the point's axis label.
    foreach ($r in $Weeks.Rows) {
        $x = [object] [string] $r['WeekLabel']
        [void] $sArea.Points.AddXY($x, [object[]] @([double] $r["${Prefix}Actions"]))
        # A week before the nightly AD load counted the groups has no Users Enabled: a gap, not a zero.
        if ($r["${Prefix}Enabled"] -is [DBNull]) {
            $i = $sEnabled.Points.AddXY($x, [object[]] @([double] 0))
            $sEnabled.Points[$i].IsEmpty = $true
        }
        else { [void] $sEnabled.Points.AddXY($x, [object[]] @([double] $r["${Prefix}Enabled"])) }
        [void] $sActive.Points.AddXY($x, [object[]] @([double] $r["${Prefix}Active"]))
    }
    $sEnabled.EmptyPointStyle.Color = [Drawing.Color]::Transparent
    $sEnabled.EmptyPointStyle.MarkerStyle = [System.Windows.Forms.DataVisualization.Charting.MarkerStyle]::None
    $sEnabled.EmptyPointStyle.IsValueShownAsLabel = $false
    # Only the last week's value is bold and larger: the one the table reports.
    foreach ($s in $sArea, $sEnabled, $sActive) {
        $last = $s.Points[$s.Points.Count - 1]
        if (-not $last.IsEmpty) { $last.Font = & $font 12 ([Drawing.FontStyle]::Bold) }
    }

    [void] $chart.Series.Add($sArea)
    [void] $chart.Series.Add($sEnabled)
    [void] $chart.Series.Add($sActive)
    $chart.SaveImage($Path, [System.Windows.Forms.DataVisualization.Charting.ChartImageFormat]::Png)
    $chart.Dispose()
}

# ===========================================================================
# The email (HTML)
# ===========================================================================

function Test-GroupCountAll {
    <# True when Users Enabled counts every group member, disabled accounts included (the dashboard's
       WeeklyReportGroupCount, Admin > Alerts and digests; 3.1.3). An older procedure has no GroupCount: enabled only. #>
    param($Meta)
    $Meta.Table.Columns.Contains('GroupCount') -and -not ($Meta.GroupCount -is [DBNull]) -and [string] $Meta.GroupCount -eq 'All'
}

function Get-GroupMembersPhrase {
    <# How Users Enabled counts the groups' members, for the notes. #>
    param($Meta)
    if (Test-GroupCountAll $Meta) { 'every member, disabled accounts included,' } else { 'the members whose AD account is enabled' }
}

function Get-MetaText {
    <# A text column of the week's row, or '' when it is NULL or an older procedure does not return it. #>
    param($Meta, [string] $Name)
    if (-not $Meta.Table.Columns.Contains($Name) -or $Meta.$Name -is [DBNull]) { return '' }
    ([string] $Meta.$Name).Trim()
}

function Get-DisabledCallout {
    <# The warning under the table (3.1.3): which numbers leave out accounts that are disabled or gone, and which keep their use. #>
    param($Meta)
    if (Test-GroupCountAll $Meta) {
        return @(
            'This report counts disabled accounts and people who have left in every row.',
            'Users Enabled counts every member of the AD groups, disabled accounts included, and Active Users everyone who used Copilot that week, including people whose account is disabled today or who are gone from the directory (set on the dashboard, Admin > Alerts and digests, or -IncludeDisabled for this run).',
            'Actions taken and Security Alerts count every interaction of the week, whoever made it, as always.',
            'So these figures are higher than a report of enabled accounts only, and Active Users can include people who no longer work here.'
        )
    }
    @(
        'Disabled accounts and people who have left are counted differently in each row.',
        'Users Enabled leaves out members of the AD groups whose account is disabled. Active Users leaves out everyone whose AD account is disabled today or who is gone from the directory, even if they used Copilot that week.',
        'Actions taken and Security Alerts count every interaction of the week, whoever made it: they do not drop when someone leaves or is disabled.',
        'So the rows answer different questions. Active Users counts people, while actions taken and Security Alerts count interactions, which can include use by people who are no longer counted in Active Users.'
    )
}

function Get-ValueDefinition {
    <# The definitions of the 8 values, one per row and column, with whether accounts that are disabled or gone count. #>
    param($Meta)
    $m365Group = Get-MetaText $Meta 'M365Group'; $chatGroup = Get-MetaText $Meta 'ChatGroup'
    if (-not $m365Group) { $m365Group = 'CopilotLicenseGroup' }
    if (-not $chatGroup) { $chatGroup = 'CopilotChatGroup' }
    $day = if ($Meta.GroupsCounted -is [DBNull]) { 'not counted this week' } else { 'counted ' + ([datetime] $Meta.GroupsCounted).ToString('M/d/yyyy', [Globalization.CultureInfo]::InvariantCulture) }
    $who = if (Test-GroupCountAll $Meta) { 'All members' } else { 'Members with an enabled account' }
    $disabledPeople = if (Test-GroupCountAll $Meta) { 'Counted (set to count everyone)' } else { 'Not counted' }
    $m365Users = "$who of $m365Group, nested groups included ($day)."
    $activeWho = if (Test-GroupCountAll $Meta) { 'Everyone who' } else { 'People with an account enabled today who' }
    if (Test-M365Estimated $Meta) {
        $holders = if (Test-GroupCountAll $Meta) { 'People' } else { 'People with an enabled account' }
        $m365Users = "ESTIMATED: $holders holding a Microsoft 365 Copilot license on $(([datetime] $Meta.WeekEnding).ToString('M/d/yyyy', [Globalization.CultureInfo]::InvariantCulture)). The AD group was not counted yet that week."
    }
    @(
        [pscustomobject] @{ Metric = 'Number of Users Enabled'; Kind = 'people, from AD'
            M365 = $m365Users
            Chat = "$who of $chatGroup, nested groups included ($day)."
            Disabled = $disabledPeople },
        [pscustomobject] @{ Metric = 'Active Users'; Kind = 'people, from use'
            M365 = "$activeWho used Copilot with a Microsoft 365 Copilot license at the time, in the week. Each person once."
            Chat = "$activeWho used Copilot Chat without a license, in the week. Each person once; someone licensed mid-week can be in both columns."
            Disabled = $disabledPeople },
        [pscustomobject] @{ Metric = 'Number of actions taken'; Kind = 'interactions'
            M365 = 'Interactions made with a license: one per prompt and its answer (one Copilot audit record). Not sessions, not people.'
            Chat = 'Interactions made without a license, counted the same way.'
            Disabled = 'Counted: all use' },
        [pscustomobject] @{ Metric = 'Security Alerts'; Kind = 'interactions'
            M365 = 'Interactions made with a license that had a jailbreak attempt, a prompt injection or a blocked resource.'
            Chat = 'The same, for interactions made without a license.'
            Disabled = 'Counted: all use' }
    )
}

function Test-M365Estimated {
    <# True when the reported week is before the groups were first counted, so M365 Copilot's Users Enabled is the license holders (3.1.3). #>
    param($Meta)
    $Meta.Table.Columns.Contains('M365Estimated') -and -not ($Meta.M365Estimated -is [DBNull]) -and [bool] $Meta.M365Estimated
}

function Get-ReportNote {
    <# The lines under the table: what is not counted, and who used Copilot outside the group of their column. #>
    param($Meta, [System.Data.DataTable] $Weeks)
    $notes = New-Object System.Collections.Generic.List[string]
    if ($Weeks -and $Weeks.Columns.Contains('M365Estimated')) {
        $est = @($Weeks.Rows | Where-Object { -not ($_.M365Estimated -is [DBNull]) -and [bool] $_.M365Estimated })
        if ($est.Count -gt 0) {
            $first = Get-MetaText $Meta 'GroupsFirstCounted'
            $since = if ($first) { ' (the AD groups were first counted on ' + ([datetime] $Meta.GroupsFirstCounted).ToString('M/d/yyyy', [Globalization.CultureInfo]::InvariantCulture) + ')' } else { '' }
            $notes.Add("M365 Copilot Users Enabled is estimated for $($est.Count) of the weeks shown$since, from the people holding a Microsoft 365 Copilot license on each week's last day. Copilot Chat Users Enabled has no such history and is blank for those weeks.")
        }
    }
    if ($Meta.GroupsCounted -is [DBNull] -and -not (Test-M365Estimated $Meta)) {
        $notes.Add('Users Enabled was not counted this week: the nightly AD load counts the two groups named on the dashboard (Admin > Directory quality) or in copilot-audit.settings.psd1, from the night they are set.')
    }
    if (-not ($Meta.M365OutsideGroup -is [DBNull]) -and [int] $Meta.M365OutsideGroup -gt 0) {
        $notes.Add(('{0} {1} Copilot with a license this week but {2} not in the Copilot license group today, so {3} in Active Users and not in Users Enabled.' -f `
            (Format-Count $Meta.M365OutsideGroup), $(if ([int] $Meta.M365OutsideGroup -eq 1) { 'person used' } else { 'people used' }),
            $(if ([int] $Meta.M365OutsideGroup -eq 1) { 'is' } else { 'are' }), $(if ([int] $Meta.M365OutsideGroup -eq 1) { 'counts' } else { 'count' })))
    }
    if (-not ($Meta.ChatOutsideGroup -is [DBNull]) -and [int] $Meta.ChatOutsideGroup -gt 0) {
        $notes.Add(('{0} {1} Copilot Chat without a license this week but {2} not in the Copilot Chat group today, so {3} in Active Users and not in Users Enabled.' -f `
            (Format-Count $Meta.ChatOutsideGroup), $(if ([int] $Meta.ChatOutsideGroup -eq 1) { 'person used' } else { 'people used' }),
            $(if ([int] $Meta.ChatOutsideGroup -eq 1) { 'is' } else { 'are' }), $(if ([int] $Meta.ChatOutsideGroup -eq 1) { 'counts' } else { 'count' })))
    }
    , $notes.ToArray()
}

function Format-StoryAttribution {
    <#
        Who said it, as the dashboard's Success stories page shows it (SuccessStories.Attribution): the name where the
        person agreed and privacy mode is off; else "A colleague in <department>" where the department has at least the
        minimum group size; else "A colleague".
    #>
    param([string] $DisplayName, [string] $Unit, [bool] $MayName, [bool] $PrivacyMode, [int] $UnitSize, [int] $MinGroupSize)
    if ($MayName -and -not $PrivacyMode -and $DisplayName.Trim()) {
        if ($Unit.Trim()) { return $DisplayName.Trim() + ', ' + $Unit.Trim() }
        return $DisplayName.Trim()
    }
    if ($Unit.Trim() -and $UnitSize -ge [Math]::Max(1, $MinGroupSize)) { return 'A colleague in ' + $Unit.Trim() }
    'A colleague'
}

function Get-ReportStory {
    <#
        The success stories for this report: approved or sent, ticked, not deleted, oldest approved first, up to
        StoriesPerReport. -All instead gives every approved or sent story, for the workbook's Stories sheet. None when the
        database has no dash.SuccessStory (before 3.1.6) or StoriesPerReport is 0.
    #>
    param($Connection, [switch] $All)
    $sql = @"
IF OBJECT_ID(N'dash.SuccessStory', N'U') IS NULL BEGIN SELECT TOP (0) 1 AS StoryId; RETURN; END;
DECLARE @n INT = TRY_CAST((SELECT Value FROM dash.Setting WHERE Name = N'StoriesPerReport') AS INT);
IF @n IS NULL SET @n = 3;
IF @n < 0 SET @n = 0;
IF @n > 10 SET @n = 10;
IF @all = 1 SET @n = 100000;
DECLARE @min INT = ISNULL(TRY_CAST((SELECT Value FROM dash.Setting WHERE Name = N'MinGroupSize') AS INT), 5);
DECLARE @privacy BIT = 0;
IF OBJECT_ID(N'audit.PipelineSetting', N'U') IS NOT NULL
    SELECT @privacy = CASE WHEN Value = N'Pseudonymize' THEN 1 ELSE 0 END FROM audit.PipelineSetting WHERE Name = N'PrivacyMode';
SELECT TOP (@n) s.StoryId, s.Quote, s.App, s.UseCase, s.MinutesSaved, s.DisplayName, s.Unit, s.MayName, s.Status, s.ApprovedUtc,
       @privacy AS PrivacyMode, @min AS MinGroupSize,
       (SELECT COUNT(*) FROM dash.Person AS p WHERE p.AccountEnabled = 1 AND p.Department = s.Unit) AS UnitSize,
       (SELECT STRING_AGG(CONVERT(NVARCHAR(10), x.SentWeek, 23), N', ') FROM dash.SuccessStorySend AS x WHERE x.StoryId = s.StoryId) AS Sends
FROM dash.SuccessStory AS s
WHERE s.Deleted = 0 AND s.Status IN ('Approved', 'Sent') AND (@all = 1 OR s.IncludeNext = 1)
ORDER BY s.ApprovedUtc, s.StoryId;
"@
    $ds = Invoke-ReportQuery $Connection $sql @{ '@all' = [bool] $All }
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($r in $ds.Tables[0].Rows) {
        if (-not $ds.Tables[0].Columns.Contains('Quote')) { break }
        $text = { param($v) if ($v -is [DBNull] -or $null -eq $v) { '' } else { [string] $v } }
        $out.Add([pscustomobject]@{
            StoryId = [int] $r.StoryId
            Quote = [string] $r.Quote
            Who = Format-StoryAttribution -DisplayName (& $text $r.DisplayName) -Unit (& $text $r.Unit) -MayName ([bool] $r.MayName) `
                -PrivacyMode ([bool] $r.PrivacyMode) -UnitSize ([int] $r.UnitSize) -MinGroupSize ([int] $r.MinGroupSize)
            App = & $text $r.App
            UseCase = & $text $r.UseCase
            MinutesSaved = if ($r.MinutesSaved -is [DBNull]) { $null } else { [int] $r.MinutesSaved }
            Status = [string] $r.Status
            Sends = & $text $r.Sends
        })
    }
    , $out.ToArray()
}

function Complete-ReportStory {
    <# After a send: each story quoted is stamped with the week, set to Sent, and its tick cleared. #>
    param($Connection, [object[]] $Stories, [datetime] $Week)
    foreach ($s in $Stories) {
        [void] (Invoke-ReportQuery $Connection @'
IF NOT EXISTS (SELECT 1 FROM dash.SuccessStorySend WHERE StoryId = @id AND SentWeek = @w)
    INSERT INTO dash.SuccessStorySend (StoryId, SentWeek) VALUES (@id, @w);
UPDATE dash.SuccessStory SET Status = 'Sent', IncludeNext = 0, UpdatedBy = N'weekly report', UpdatedUtc = SYSUTCDATETIME()
WHERE StoryId = @id AND Status IN ('Approved', 'Sent');
'@ @{ '@id' = $s.StoryId; '@w' = $Week.Date })
    }
}

function Get-StoryDetail {
    param($Story)
    $parts = @($Story.App, $Story.UseCase) | Where-Object { $_ }
    if ($null -ne $Story.MinutesSaved) { $parts += ('{0:N0} minutes saved a week' -f $Story.MinutesSaved) }
    $parts -join ' - '
}

function Send-StoryReminder {
    <#
        The day before the send day, from the send time: one reminder a week to StoriesReminderTo (dashboard setting) when
        stories wait for review or none is ticked. Recorded in dash.Setting StoriesReminderWeek, so it goes once a week.
    #>
    param($Connection, [datetime] $Week)
    $ds = Invoke-ReportQuery $Connection @'
IF OBJECT_ID(N'dash.SuccessStory', N'U') IS NULL BEGIN SELECT CAST(NULL AS NVARCHAR(400)) AS ToList, 0 AS Waiting, 0 AS Ticked, CAST(NULL AS NVARCHAR(400)) AS Done; RETURN; END;
SELECT (SELECT Value FROM dash.Setting WHERE Name = N'StoriesReminderTo') AS ToList,
       (SELECT COUNT(*) FROM dash.SuccessStory WHERE Deleted = 0 AND Status = 'Entered') AS Waiting,
       (SELECT COUNT(*) FROM dash.SuccessStory WHERE Deleted = 0 AND IncludeNext = 1 AND Status IN ('Approved', 'Sent')) AS Ticked,
       (SELECT Value FROM dash.Setting WHERE Name = N'StoriesReminderWeek') AS Done;
'@
    $r = $ds.Tables[0].Rows[0]
    $to = if ($r.ToList -is [DBNull]) { @() } else { @(([string] $r.ToList) -split '[;,]' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    $stamp = $Week.ToString('yyyy-MM-dd')
    if ($to.Count -eq 0 -or (($r.Done -isnot [DBNull]) -and [string] $r.Done -eq $stamp)) { return }
    $waiting = [int] $r.Waiting; $ticked = [int] $r.Ticked
    if ($waiting -eq 0 -and $ticked -gt 0) { return }
    $lines = @()
    if ($waiting -gt 0) { $lines += "$waiting success $(if ($waiting -eq 1) { 'story waits' } else { 'stories wait' }) for review." }
    $lines += if ($ticked -eq 0) { 'No story is ticked for tomorrow''s weekly report, so it will have no "In their words".' } else { "$ticked $(if ($ticked -eq 1) { 'story is' } else { 'stories are' }) ticked for tomorrow's weekly report." }
    $lines += 'Review them on the dashboard: Success stories.'
    $text = $lines -join "`r`n"
    $html = '<div style="font-family:''Segoe UI'',Arial,sans-serif;font-size:14px;">' + (($lines | ForEach-Object { ConvertTo-Html5Text $_ }) -join '<br>') + '</div>'
    try {
        $mail = Get-DashboardMail $Connection
        Send-ReportMail -Mail $mail -Subject 'Success stories for tomorrow''s Copilot weekly report' -Html $html -Text $text -ImageFiles @{} -Workbook $null -ToList $to -CcList @() -BccList @()
        [void] (Invoke-ReportQuery $Connection @'
UPDATE dash.Setting SET Value = @w, UpdatedBy = N'weekly report', UpdatedUtc = SYSUTCDATETIME() WHERE Name = N'StoriesReminderWeek';
IF @@ROWCOUNT = 0 INSERT INTO dash.Setting (Name, Value, Description, UpdatedBy)
    VALUES (N'StoriesReminderWeek', @w, N'The week the success stories reminder last went for (the weekly report keeps it).', N'weekly report');
'@ @{ '@w' = $stamp })
        Write-ReportLog "Success stories reminder sent to $($to -join '; ') ($waiting waiting, $ticked ticked)."
    }
    catch { Write-ReportLog "The success stories reminder was not sent: $($_.Exception.Message)" 'WARN' }
}

function Get-ReportSaving {
    # The money line under the heading: saved to date through the dashboard, and the high-confidence savings found a
    # month, from the saving ledger (dash.SavingLedger). Nothing when the ledger is not installed or holds nothing, or
    # when the dashboard setting WeeklyReportSavings is 0.
    param($Connection)
    $sql = @"
IF OBJECT_ID(N'dash.SavingLedger', N'U') IS NULL OR (SELECT Value FROM dash.Setting WHERE Name = N'WeeklyReportSavings') = N'0'
BEGIN SELECT TOP (0) CAST(0 AS DECIMAL(19, 2)) AS Saved, CAST(0 AS DECIMAL(19, 2)) AS Found, N'' AS Currency; RETURN; END;
DECLARE @fm DATE = (SELECT MAX([Month]) FROM dash.SavingLedger WHERE Kind = 'Found');
SELECT ISNULL((SELECT SUM(Amount) FROM dash.SavingLedger WHERE Kind = 'Saved'), 0) AS Saved,
       ISNULL((SELECT SUM(Amount) FROM dash.SavingLedger WHERE Kind = 'Found' AND [Month] = @fm AND Confidence = 'High'), 0) AS Found,
       ISNULL((SELECT TOP (1) Currency FROM dash.SavingLedger ORDER BY [Month] DESC, WrittenUtc DESC), N'USD') AS Currency;
"@
    $t = (Invoke-ReportQuery $Connection $sql).Tables[0]
    if ($t.Rows.Count -eq 0) { return $null }
    $r = $t.Rows[0]
    Format-SavingLine -Saved ([decimal] $r.Saved) -Found ([decimal] $r.Found) -Currency ([string] $r.Currency)
}

function Format-SavingLine {
    param([decimal] $Saved, [decimal] $Found, [string] $Currency)
    if ($Saved -le 0 -and $Found -le 0) { return $null }
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $money = { param([decimal] $v) [Math]::Round($v, 0).ToString('#,0', $inv) + ' ' + $Currency }
    $line = if ($Saved -gt 0) { "$(& $money $Saved) saved to date through the Copilot dashboard" } else { 'Nothing saved through the Copilot dashboard yet' }
    if ($Found -gt 0) { $line += ", and $(& $money $Found) a month more found with high confidence (Leadership > Costs and savings)." } else { $line += '.' }
    $line
}

function New-ReportHtml {    param($Meta, [System.Data.DataTable] $Table, [hashtable] $Images, [string[]] $Warnings, [System.Data.DataTable] $Weeks, [object[]] $Stories = @(), [string] $Saving)
    $p = $script:Palette
    $f = "font-family:'Segoe UI',Arial,sans-serif;"
    $weekLabel = ConvertTo-Html5Text ([string] $Meta.WeekLabel)
    $through = ([datetime] $Meta.DataThrough).ToString('M/d/yyyy', [Globalization.CultureInfo]::InvariantCulture)
    $zone = ConvertTo-Html5Text ([string] $Meta.ReportingTimeZone)

    $sb = New-Object System.Text.StringBuilder
    [void] $sb.Append("<!DOCTYPE html><html><head><meta charset=`"utf-8`"></head><body style=`"margin:0;padding:0;background:#ffffff;`">")
    [void] $sb.Append("<table role=`"presentation`" cellpadding=`"0`" cellspacing=`"0`" border=`"0`" width=`"920`" style=`"$f color:$($p.Text);`"><tr><td style=`"padding:16px 10px;`">")
    [void] $sb.Append("<div style=`"$f font-size:20px;font-weight:700;color:$($p.Navy);`">Copilot Chat and M365 Copilot: Adoption and Security</div>")
    [void] $sb.Append("<div style=`"$f font-size:13px;color:$($p.Muted);padding:4px 0 14px 0;`">Week of $weekLabel (Saturday to Friday, $zone). Data through $through.</div>")
    if ($Saving) { [void] $sb.Append("<div style=`"$f font-size:14px;color:$($p.Text);padding:0 0 14px 0;`"><b>Money found.</b> $(ConvertTo-Html5Text $Saving)</div>") }

    foreach ($w in $Warnings) {
        [void] $sb.Append("<div style=`"$f font-size:13px;background:#FFF4CE;border-left:4px solid #E3A400;padding:8px 12px;margin:0 0 12px 0;`">$(ConvertTo-Html5Text $w)</div>")
    }

    $th = "$f font-size:14px;font-weight:700;padding:9px 12px;text-align:center;border:1px solid #ffffff;"
    $td = "$f font-size:15px;padding:9px 12px;text-align:center;border:1px solid #ffffff;"
    [void] $sb.Append("<table cellpadding=`"0`" cellspacing=`"0`" border=`"0`" style=`"border-collapse:collapse;width:640px;`">")
    [void] $sb.Append("<tr><td rowspan=`"2`" style=`"$th background:$($p.HeadDark);width:260px;`"></td>")
    [void] $sb.Append("<td colspan=`"2`" style=`"$th background:$($p.HeadDark);color:#ffffff;font-size:16px;`">Week of $weekLabel</td></tr>")
    [void] $sb.Append("<tr><td style=`"$th background:$($p.SubHead);color:$($p.Text);`">M365 Copilot</td><td style=`"$th background:$($p.SubHead);color:$($p.Text);`">Copilot Chat</td></tr>")
    $i = 0
    foreach ($r in $Table.Rows) {
        $bg = if ($i % 2 -eq 0) { $p.RowA } else { $p.RowB }
        [void] $sb.Append("<tr><td style=`"$td background:$($p.Label);color:#ffffff;text-align:left;font-size:15px;`">$(ConvertTo-Html5Text ([string] $r.Metric))</td>")
        [void] $sb.Append("<td style=`"$td background:$bg;font-weight:600;`">$(Format-Count $r.WeekM365)</td><td style=`"$td background:$bg;font-weight:600;`">$(Format-Count $r.WeekChat)</td></tr>")
        $i++
    }
    [void] $sb.Append('</table>')

    $n = "$f font-size:12px;color:$($p.Muted);line-height:1.5;"
    # What each of the 8 numbers is, straight under them, with the rule on disabled accounts first (3.1.3).
    $callout = Get-DisabledCallout $Meta
    [void] $sb.Append("<div style=`"$f font-size:13px;line-height:1.5;color:$($p.Text);background:#FFF4CE;border-left:5px solid #E3A400;padding:10px 14px;margin:14px 0 0 0;width:880px;`">")
    [void] $sb.Append("<b>Read this first: $(ConvertTo-Html5Text $callout[0])</b><br>")
    [void] $sb.Append(($callout[1..($callout.Count - 1)] | ForEach-Object { ConvertTo-Html5Text $_ }) -join '<br>')
    [void] $sb.Append('</div>')
    $dh = "$f font-size:12px;font-weight:700;padding:7px 9px;text-align:left;vertical-align:bottom;background:$($p.SubHead);color:$($p.Text);border:1px solid #ffffff;"
    $dd = "$f font-size:12px;line-height:1.4;padding:7px 9px;text-align:left;vertical-align:top;border:1px solid #ffffff;"
    [void] $sb.Append("<div style=`"$f font-size:14px;font-weight:700;color:$($p.Navy);padding:14px 0 6px 0;`">What each number means</div>")
    [void] $sb.Append("<table cellpadding=`"0`" cellspacing=`"0`" border=`"0`" style=`"border-collapse:collapse;width:908px;`">")
    [void] $sb.Append("<tr><td style=`"$dh width:170px;`">Row</td><td style=`"$dh width:300px;`">M365 Copilot (with a license)</td><td style=`"$dh width:300px;`">Copilot Chat (without a license)</td><td style=`"$dh width:138px;`">Disabled accounts and leavers</td></tr>")
    $i = 0
    foreach ($d in (Get-ValueDefinition $Meta)) {
        $bg = if ($i % 2 -eq 0) { $p.RowA } else { $p.RowB }
        $mark = if ($d.Disabled -like 'Not*') { "color:#A4262C;" } else { "color:#107C10;" }
        [void] $sb.Append("<tr><td style=`"$dd background:$bg;`"><b>$(ConvertTo-Html5Text $d.Metric)</b><br><span style=`"color:$($p.Muted);`">$(ConvertTo-Html5Text $d.Kind)</span></td>")
        [void] $sb.Append("<td style=`"$dd background:$bg;`">$(ConvertTo-Html5Text $d.M365)</td><td style=`"$dd background:$bg;`">$(ConvertTo-Html5Text $d.Chat)</td>")
        [void] $sb.Append("<td style=`"$dd background:$bg;font-weight:700;$mark`">$(ConvertTo-Html5Text $d.Disabled)</td></tr>")
        $i++
    }
    [void] $sb.Append('</table>')
    foreach ($note in (Get-ReportNote $Meta $Weeks)) { [void] $sb.Append("<div style=`"$n padding:6px 0 0 0;`">$(ConvertTo-Html5Text $note)</div>") }

    foreach ($key in 'Chat', 'M365') {
        if ($Images -and $Images.ContainsKey($key)) {
            [void] $sb.Append("<div style=`"padding:22px 0 0 0;`"><img src=`"$($Images[$key])`" width=`"900`" height=`"380`" alt=`"$key chart`" style=`"display:block;border:1px solid #E3E8EE;`"></div>")
        }
    }

    if ($Stories.Count) {
        [void] $sb.Append("<div style=`"$f font-size:16px;font-weight:700;color:$($p.Navy);padding:24px 0 6px 0;`">In their words</div>")
        foreach ($s in $Stories) {
            $detail = Get-StoryDetail $s
            [void] $sb.Append("<div style=`"$f border-left:4px solid $($p.Orange);background:$($p.RowA);padding:10px 14px;margin:0 0 10px 0;width:870px;`">")
            [void] $sb.Append("<div style=`"$f font-size:15px;line-height:1.5;color:$($p.Text);`">$([char] 0x201C)$(ConvertTo-Html5Text $s.Quote)$([char] 0x201D)</div>")
            [void] $sb.Append("<div style=`"$f font-size:12px;color:$($p.Muted);padding:4px 0 0 0;`">$([char] 0x2014) $(ConvertTo-Html5Text $s.Who)$(if ($detail) { ' &middot; ' + (ConvertTo-Html5Text $detail) })</div></div>")
        }
    }

    [void] $sb.Append("<div style=`"$n padding:18px 0 0 0;`">The charts show the same figures for each of the last weeks. An Excel workbook with the figures, the definitions and the charts is attached.</div>")
    [void] $sb.Append('</td></tr></table></body></html>')
    $sb.ToString()
}

function New-ReportText {
    param($Meta, [System.Data.DataTable] $Table, [System.Data.DataTable] $Weeks, [object[]] $Stories = @(), [string] $Saving)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("Copilot weekly report, week of $($Meta.WeekLabel)")
    if ($Saving) { $lines.Add(''); $lines.Add("Money found. $Saving") }
    $lines.Add('')
    $lines.Add(('{0,-26}{1,14}{2,14}' -f '', 'M365 Copilot', 'Copilot Chat'))
    foreach ($r in $Table.Rows) {
        $lines.Add(('{0,-26}{1,14}{2,14}' -f $r.Metric, (Format-Count $r.WeekM365), (Format-Count $r.WeekChat)))
    }
    $callout = Get-DisabledCallout $Meta
    $lines.Add('')
    $lines.Add("READ THIS FIRST: $($callout[0])")
    foreach ($c in $callout[1..($callout.Count - 1)]) { $lines.Add("  $c") }
    $lines.Add('')
    $lines.Add('WHAT EACH NUMBER MEANS')
    foreach ($d in (Get-ValueDefinition $Meta)) {
        $lines.Add('')
        $lines.Add("$($d.Metric) ($($d.Kind)). Disabled accounts and leavers: $($d.Disabled).")
        $lines.Add("  M365 Copilot: $($d.M365)")
        $lines.Add("  Copilot Chat: $($d.Chat)")
    }
    foreach ($note in (Get-ReportNote $Meta $Weeks)) { $lines.Add(''); $lines.Add($note) }
    if ($Stories.Count) {
        $lines.Add('')
        $lines.Add('IN THEIR WORDS')
        foreach ($s in $Stories) {
            $detail = Get-StoryDetail $s
            $lines.Add('')
            $lines.Add('"' + $s.Quote + '"')
            $lines.Add('  - ' + $s.Who + $(if ($detail) { " ($detail)" }))
        }
    }
    $lines.Add('')
    $lines.Add('The charts and the workbook are in the HTML version of this message.')
    $lines -join "`r`n"
}

# ===========================================================================
# The workbook (EPPlus, through ImportExcel)
# ===========================================================================

function New-ComboChartXml {
    <#
        A complete chart part: users enabled and active users as lines on the
        left axis, actions taken as an area on the right axis. Written out in
        full, in the schema's element order, because this EPPlus writes a
        series' fill after its values and categories as numbers, which Excel
        repairs on open.
    #>
    param([string] $Title, [string] $Sheet, [string] $Categories,
          [string] $Enabled, [string] $EnabledName, [string] $Active, [string] $ActiveName,
          [string] $Actions, [string] $ActionsName, [double] $UsersMax, [double] $ActionsMax)
    $e = { param([string] $t) [System.Security.SecurityElement]::Escape($t) }
    $ref = { param([string] $r) "'" + $Sheet + "'!" + ((($r -split ':') | ForEach-Object { '$' + ($_ -replace '(\d+)', '$$$1') }) -join ':') }
    $font = { param([int] $size, [string] $hex, [bool] $bold)
        $b = if ($bold) { ' b="1"' } else { ' b="0"' }
        "<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=`"$size`"$b><a:solidFill><a:srgbClr val=`"$hex`"/></a:solidFill><a:latin typeface=`"Segoe UI`"/></a:defRPr></a:pPr><a:endParaRPr lang=`"en-US`"/></a:p></c:txPr>" }
    $axTitle = { param([string] $t)
        "<c:title><c:tx><c:rich><a:bodyPr rot=`"-5400000`" vert=`"horz`"/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=`"900`" b=`"0`"><a:solidFill><a:srgbClr val=`"5F6B76`"/></a:solidFill></a:defRPr></a:pPr><a:r><a:rPr lang=`"en-US`" sz=`"900`" b=`"0`"><a:solidFill><a:srgbClr val=`"5F6B76`"/></a:solidFill></a:rPr><a:t>$(& $e $t)</a:t></a:r></a:p></c:rich></c:tx><c:overlay val=`"0`"/></c:title>" }
    $axTitleH = { param([string] $t)
        "<c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=`"900`" b=`"0`"><a:solidFill><a:srgbClr val=`"5F6B76`"/></a:solidFill></a:defRPr></a:pPr><a:r><a:rPr lang=`"en-US`" sz=`"900`" b=`"0`"><a:solidFill><a:srgbClr val=`"5F6B76`"/></a:solidFill></a:rPr><a:t>$(& $e $t)</a:t></a:r></a:p></c:rich></c:tx><c:overlay val=`"0`"/></c:title>" }
    $cat = "<c:cat><c:strRef><c:f>$(& $e (& $ref $Categories))</c:f></c:strRef></c:cat>"
    $lineSer = { param([int] $i, [string] $range, [string] $name, [string] $hex, [string] $pos)
        "<c:ser><c:idx val=`"$i`"/><c:order val=`"$i`"/><c:tx><c:v>$(& $e $name)</c:v></c:tx>" +
        "<c:spPr><a:ln w=`"31750`" cap=`"rnd`"><a:solidFill><a:srgbClr val=`"$hex`"/></a:solidFill><a:round/></a:ln></c:spPr>" +
        "<c:marker><c:symbol val=`"circle`"/><c:size val=`"6`"/><c:spPr><a:solidFill><a:srgbClr val=`"FFFFFF`"/></a:solidFill><a:ln w=`"19050`"><a:solidFill><a:srgbClr val=`"$hex`"/></a:solidFill></a:ln></c:spPr></c:marker>" +
        "<c:dLbls><c:numFmt formatCode=`"#,##0`" sourceLinked=`"0`"/><c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr>$(& $font 800 $hex $true)" +
        "<c:dLblPos val=`"$pos`"/><c:showLegendKey val=`"0`"/><c:showVal val=`"1`"/><c:showCatName val=`"0`"/><c:showSerName val=`"0`"/><c:showPercent val=`"0`"/><c:showBubbleSize val=`"0`"/></c:dLbls>" +
        $cat + "<c:val><c:numRef><c:f>$(& $e (& $ref $range))</c:f></c:numRef></c:val><c:smooth val=`"0`"/></c:ser>" }

    $xml = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
        '<c:chartSpace xmlns:c="http://schemas.openxmlformats.org/drawingml/2006/chart" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">' +
        '<c:roundedCorners val="0"/><c:chart>' +
        "<c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr sz=`"1300`" b=`"1`"/></a:pPr><a:r><a:rPr lang=`"en-US`" sz=`"1300`" b=`"1`"><a:solidFill><a:srgbClr val=`"1F3B5A`"/></a:solidFill><a:latin typeface=`"Segoe UI`"/></a:rPr><a:t>$(& $e $Title)</a:t></a:r></a:p></c:rich></c:tx><c:overlay val=`"0`"/></c:title>" +
        '<c:autoTitleDeleted val="0"/><c:plotArea><c:layout/>' +
        # the area first, so it is drawn behind the lines
        '<c:areaChart><c:grouping val="standard"/><c:varyColors val="0"/>' +
        "<c:ser><c:idx val=`"2`"/><c:order val=`"2`"/><c:tx><c:v>$(& $e $ActionsName)</c:v></c:tx>" +
        '<c:spPr><a:solidFill><a:srgbClr val="BCCFE0"><a:alpha val="85000"/></a:srgbClr></a:solidFill><a:ln w="19050"><a:solidFill><a:srgbClr val="8FAAC4"/></a:solidFill></a:ln></c:spPr>' +
        "<c:dLbls><c:numFmt formatCode=`"#,##0`" sourceLinked=`"0`"/><c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr>$(& $font 800 '46586A' $false)" +
        '<c:showLegendKey val="0"/><c:showVal val="1"/><c:showCatName val="0"/><c:showSerName val="0"/><c:showPercent val="0"/><c:showBubbleSize val="0"/></c:dLbls>' +
        $cat + "<c:val><c:numRef><c:f>$(& $e (& $ref $Actions))</c:f></c:numRef></c:val></c:ser>" +
        '<c:axId val="30"/><c:axId val="40"/></c:areaChart>' +
        '<c:lineChart><c:grouping val="standard"/><c:varyColors val="0"/>' +
        (& $lineSer 0 $Enabled $EnabledName '1F3B5A' 't') + (& $lineSer 1 $Active $ActiveName 'E8743B' 'b') +
        '<c:marker val="1"/><c:axId val="10"/><c:axId val="20"/></c:lineChart>' +
        # users: the visible category axis and the left value axis
        '<c:catAx><c:axId val="10"/><c:scaling><c:orientation val="minMax"/></c:scaling><c:delete val="0"/><c:axPos val="b"/>' +
        (& $axTitleH 'Week of (Saturday)') + '<c:numFmt formatCode="General" sourceLinked="1"/><c:majorTickMark val="none"/><c:minorTickMark val="none"/><c:tickLblPos val="nextTo"/>' +
        '<c:spPr><a:ln w="9525"><a:solidFill><a:srgbClr val="B8C2CC"/></a:solidFill></a:ln></c:spPr>' + (& $font 800 '5F6B76' $false) +
        '<c:crossAx val="20"/><c:crosses val="autoZero"/><c:auto val="1"/><c:lblAlgn val="ctr"/><c:lblOffset val="100"/><c:noMultiLvlLbl val="0"/></c:catAx>' +
        "<c:valAx><c:axId val=`"20`"/><c:scaling><c:orientation val=`"minMax`"/><c:max val=`"$UsersMax`"/><c:min val=`"0`"/></c:scaling><c:delete val=`"0`"/><c:axPos val=`"l`"/>" +
        '<c:majorGridlines><c:spPr><a:ln w="9525"><a:solidFill><a:srgbClr val="E3E8EE"/></a:solidFill></a:ln></c:spPr></c:majorGridlines>' +
        (& $axTitle 'Users') + '<c:numFmt formatCode="#,##0" sourceLinked="0"/><c:majorTickMark val="none"/><c:minorTickMark val="none"/><c:tickLblPos val="nextTo"/>' +
        '<c:spPr><a:ln><a:noFill/></a:ln></c:spPr>' + (& $font 800 '5F6B76' $false) +
        "<c:crossAx val=`"10`"/><c:crosses val=`"autoZero`"/><c:crossBetween val=`"between`"/><c:majorUnit val=`"$($UsersMax / 5)`"/></c:valAx>" +
        # actions: a hidden category axis and the right value axis
        '<c:catAx><c:axId val="30"/><c:scaling><c:orientation val="minMax"/></c:scaling><c:delete val="1"/><c:axPos val="b"/><c:majorTickMark val="none"/><c:minorTickMark val="none"/><c:tickLblPos val="nextTo"/>' +
        '<c:crossAx val="40"/><c:crosses val="autoZero"/><c:auto val="1"/><c:lblAlgn val="ctr"/><c:lblOffset val="100"/><c:noMultiLvlLbl val="0"/></c:catAx>' +
        "<c:valAx><c:axId val=`"40`"/><c:scaling><c:orientation val=`"minMax`"/><c:max val=`"$ActionsMax`"/><c:min val=`"0`"/></c:scaling><c:delete val=`"0`"/><c:axPos val=`"r`"/>" +
        (& $axTitle 'Actions taken') + '<c:numFmt formatCode="#,##0" sourceLinked="0"/><c:majorTickMark val="none"/><c:minorTickMark val="none"/><c:tickLblPos val="nextTo"/>' +
        '<c:spPr><a:ln><a:noFill/></a:ln></c:spPr>' + (& $font 800 '5F6B76' $false) +
        "<c:crossAx val=`"30`"/><c:crosses val=`"max`"/><c:crossBetween val=`"between`"/><c:majorUnit val=`"$($ActionsMax / 5)`"/></c:valAx>" +
        '<c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr></c:plotArea>' +
        '<c:legend><c:legendPos val="b"/><c:overlay val="0"/>' + (& $font 900 '1B1B1B' $false) + '</c:legend>' +
        '<c:plotVisOnly val="1"/><c:dispBlanksAs val="gap"/></c:chart>' +
        '<c:spPr><a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill><a:ln w="9525"><a:solidFill><a:srgbClr val="E3E8EE"/></a:solidFill></a:ln></c:spPr>' +
        '<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr><a:defRPr><a:latin typeface="Segoe UI"/></a:defRPr></a:pPr><a:endParaRPr lang="en-US"/></a:p></c:txPr>' +
        '</c:chartSpace>'
    $xml
}

function New-ReportWorkbook {
    param($Meta, [System.Data.DataTable] $Table, [System.Data.DataTable] $Weeks, [string] $Path, [object[]] $Library = @())
    Import-Module ImportExcel -ErrorAction Stop
    $color = { param([string] $hex) [Drawing.ColorTranslator]::FromHtml($hex) }
    $p = $script:Palette

    $pkg = New-Object OfficeOpenXml.ExcelPackage
    try {
        $ws = $pkg.Workbook.Worksheets.Add('Summary')
        $wd = $pkg.Workbook.Worksheets.Add('Weekly')
        $wn = $pkg.Workbook.Worksheets.Add('Notes')
        foreach ($sheet in $ws, $wd, $wn) { $sheet.Cells.Style.Font.Name = 'Segoe UI'; $sheet.Cells.Style.Font.Size = 10 }

        # ---- Weekly: one row per week ------------------------------------------
        $heads = 'Week of', 'Week start', 'Week ending',
                 'M365 Copilot users enabled', 'M365 Copilot active users', 'M365 Copilot actions taken', 'M365 Copilot security alerts',
                 'Copilot Chat users enabled', 'Copilot Chat active users', 'Copilot Chat actions taken', 'Copilot Chat security alerts',
                 'M365 Copilot users enabled from'
        $cols = 'WeekLabel', 'WeekStart', 'WeekEnding', 'M365Enabled', 'M365Active', 'M365Actions', 'M365Signals',
                'ChatEnabled', 'ChatActive', 'ChatActions', 'ChatSignals', 'M365Source'
        for ($c = 0; $c -lt $heads.Count; $c++) { $wd.Cells[1, ($c + 1)].Value = $heads[$c] }
        $row = 2
        foreach ($r in $Weeks.Rows) {
            for ($c = 0; $c -lt $cols.Count; $c++) {
                if ($cols[$c] -eq 'M365Source') {
                    # Where the week's M365 Copilot users enabled comes from (3.1.3): the AD group, or the license holders before it was counted.
                    $v = if ($r['M365Enabled'] -is [DBNull]) { $null }
                         elseif ($Weeks.Columns.Contains('M365Estimated') -and -not ($r['M365Estimated'] -is [DBNull]) -and [bool] $r['M365Estimated']) { 'Estimated from licenses' }
                         else { 'AD group' }
                    $wd.Cells[$row, ($c + 1)].Value = $v
                    continue
                }
                $v = $r[$cols[$c]]
                if ($v -is [DBNull]) { $v = $null }
                elseif ($c -ge 3) { $v = [double] $v }
                $wd.Cells[$row, ($c + 1)].Value = $v
            }
            $row++
        }
        $lastRow = $row - 1
        $wd.Cells["B2:C$lastRow"].Style.Numberformat.Format = 'm/d/yyyy'
        $wd.Cells["D2:K$lastRow"].Style.Numberformat.Format = '#,##0'
        $tbl = $wd.Tables.Add($wd.Cells["A1:L$lastRow"], 'WeeklyFigures')
        $tbl.TableStyle = [OfficeOpenXml.Table.TableStyles]::Medium2
        $wd.Column(1).Width = 14; $wd.Column(2).Width = 12; $wd.Column(3).Width = 12
        for ($c = 4; $c -le 11; $c++) { $wd.Column($c).Width = 17 }
        $wd.Column(12).Width = 22
        $wd.Row(1).Height = 32
        $wd.Cells['A1:L1'].Style.WrapText = $true
        $wd.View.FreezePanes(2, 2)

        # ---- Summary: the table, as in the email -----------------------------
        $ws.View.ShowGridLines = $false
        $ws.Column(1).Width = 2
        $ws.Column(2).Width = 30
        for ($c = 3; $c -le 4; $c++) { $ws.Column($c).Width = 34 }
        $through = ([datetime] $Meta.DataThrough).ToString('M/d/yyyy', [Globalization.CultureInfo]::InvariantCulture)
        $ws.Cells['B2'].Value = 'Copilot Chat and M365 Copilot: Adoption and Security'
        $ws.Cells['B2'].Style.Font.Size = 15
        $ws.Cells['B2'].Style.Font.Bold = $true
        $ws.Cells['B2'].Style.Font.Color.SetColor((& $color $p.Navy))
        $ws.Cells['B3'].Value = "Week of $($Meta.WeekLabel) (Saturday to Friday, $($Meta.ReportingTimeZone)). Data through $through."
        $ws.Cells['B3'].Style.Font.Color.SetColor((& $color $p.Muted))

        $ws.Cells['C5:D5'].Merge = $true; $ws.Cells['C5'].Value = "Week of $($Meta.WeekLabel)"
        $ws.Cells['B5:B6'].Merge = $true
        $ws.Cells['C6'].Value = 'M365 Copilot'; $ws.Cells['D6'].Value = 'Copilot Chat'
        $fill = {
            param($range, [string] $hex, [string] $fontHex, [bool] $bold)
            $range.Style.Fill.PatternType = [OfficeOpenXml.Style.ExcelFillStyle]::Solid
            $range.Style.Fill.BackgroundColor.SetColor((& $color $hex))
            $range.Style.Font.Color.SetColor((& $color $fontHex))
            $range.Style.Font.Bold = $bold
        }
        & $fill $ws.Cells['B5:D5'] $p.HeadDark '#FFFFFF' $true
        & $fill $ws.Cells['B6'] $p.HeadDark '#FFFFFF' $true
        & $fill $ws.Cells['C6:D6'] $p.SubHead $p.Text $true
        $ws.Cells['C5:D5'].Style.Font.Size = 12
        $row = 7; $i = 0
        foreach ($r in $Table.Rows) {
            $ws.Cells[$row, 2].Value = [string] $r.Metric
            foreach ($pair in @(@(3, $r.WeekM365), @(4, $r.WeekChat))) {
                $ws.Cells[$row, $pair[0]].Value = $(if ($pair[1] -is [DBNull]) { $null } else { [double] $pair[1] })
            }
            $bg = if ($i % 2 -eq 0) { $p.RowA } else { $p.RowB }
            & $fill $ws.Cells[$row, 2] $p.Label '#FFFFFF' $false
            $ws.Cells[$row, 2].Style.Indent = 1
            & $fill $ws.Cells[$row, 3, $row, 4] $bg $p.Text $true
            $row++; $i++
        }
        $ws.Cells['C7:D10'].Style.Numberformat.Format = '#,##0'
        $ws.Cells['C5:D10'].Style.HorizontalAlignment = [OfficeOpenXml.Style.ExcelHorizontalAlignment]::Center
        $ws.Cells['B5:D10'].Style.VerticalAlignment = [OfficeOpenXml.Style.ExcelVerticalAlignment]::Center
        $ws.Cells['B7:D10'].Style.Font.Size = 12
        for ($rr = 5; $rr -le 10; $rr++) { $ws.Row($rr).Height = 24 }
        $border = $ws.Cells['B5:D10'].Style.Border
        foreach ($b in $border.Top, $border.Bottom, $border.Left, $border.Right) {
            $b.Style = [OfficeOpenXml.Style.ExcelBorderStyle]::Thin
            $b.Color.SetColor([Drawing.Color]::White)
        }
        # Under the table, as in the email: the rule on disabled accounts, then what each of the 8 numbers is (3.1.3).
        $ws.Column(5).Width = 22
        $row = 12
        $callout = Get-DisabledCallout $Meta
        $box = $ws.Cells[$row, 2, ($row + $callout.Count - 1), 5]
        & $fill $box '#FFF4CE' $p.Text $false
        $box.Style.Border.Left.Style = [OfficeOpenXml.Style.ExcelBorderStyle]::Thick
        $box.Style.Border.Left.Color.SetColor((& $color '#E3A400'))
        for ($k = 0; $k -lt $callout.Count; $k++) {
            $line = $ws.Cells[($row + $k), 2, ($row + $k), 5]
            $line.Merge = $true
            $line.Style.WrapText = $true
            $line.Value = $(if ($k -eq 0) { "Read this first: $($callout[0])" } else { $callout[$k] })
            $ws.Row($row + $k).Height = $(if ($k -eq 0) { 18 } else { 30 })
        }
        $ws.Cells[$row, 2].Style.Font.Bold = $true
        $row += $callout.Count + 1
        $ws.Cells[$row, 2].Value = 'What each number means'
        $ws.Cells[$row, 2].Style.Font.Bold = $true; $ws.Cells[$row, 2].Style.Font.Size = 12
        $ws.Cells[$row, 2].Style.Font.Color.SetColor((& $color $p.Navy))
        $row++
        $defHeads = 'Row', 'M365 Copilot (with a license)', 'Copilot Chat (without a license)', 'Disabled accounts and leavers'
        for ($c = 0; $c -lt 4; $c++) { $ws.Cells[$row, ($c + 2)].Value = $defHeads[$c] }
        & $fill $ws.Cells[$row, 2, $row, 5] $p.SubHead $p.Text $true
        $ws.Cells[$row, 2, $row, 5].Style.WrapText = $true
        $ws.Row($row).Height = 30
        $row++; $i = 0
        foreach ($d in (Get-ValueDefinition $Meta)) {
            $ws.Cells[$row, 2].Value = "$($d.Metric) ($($d.Kind))"
            $ws.Cells[$row, 3].Value = $d.M365
            $ws.Cells[$row, 4].Value = $d.Chat
            $ws.Cells[$row, 5].Value = $d.Disabled
            $bg = if ($i % 2 -eq 0) { $p.RowA } else { $p.RowB }
            & $fill $ws.Cells[$row, 2, $row, 5] $bg $p.Text $false
            $ws.Cells[$row, 2].Style.Font.Bold = $true
            $ws.Cells[$row, 5].Style.Font.Bold = $true
            $ws.Cells[$row, 5].Style.Font.Color.SetColor((& $color $(if ($d.Disabled -like 'Not*') { '#A4262C' } else { '#107C10' })))
            $ws.Cells[$row, 2, $row, 5].Style.WrapText = $true
            $ws.Cells[$row, 2, $row, 5].Style.VerticalAlignment = [OfficeOpenXml.Style.ExcelVerticalAlignment]::Top
            $ws.Row($row).Height = 58
            $row++; $i++
        }
        $row++
        foreach ($note in (Get-ReportNote $Meta $Weeks)) {
            $ws.Cells[$row, 2, $row, 5].Merge = $true
            $ws.Cells[$row, 2].Value = $note
            $ws.Cells[$row, 2].Style.WrapText = $true
            $ws.Cells[$row, 2].Style.Font.Color.SetColor((& $color $p.Muted))
            $ws.Row($row).Height = 30
            $row++
        }
        $chartTop = $row + 2

        # ---- the two charts: native Excel charts over the Weekly sheet -------
        $specs = @(
            @{ Name = 'ChatChart'; Prefix = 'Chat'; Title = 'Copilot Chat Enabled vs. Active vs. Actions Taken'; Top = $chartTop
               Enabled = "H2:H$lastRow"; Active = "I2:I$lastRow"; Actions = "J2:J$lastRow"
               EnabledName = 'Chat Users Enabled'; ActiveName = 'Chat Users Active'; ActionsName = 'Chat Actions Taken' },
            @{ Name = 'M365Chart'; Prefix = 'M365'; Title = 'M365 Copilot Users Enabled vs. Active vs. Actions Taken'; Top = ($chartTop + 23)
               Enabled = "D2:D$lastRow"; Active = "E2:E$lastRow"; Actions = "F2:F$lastRow"
               EnabledName = 'M365 Copilot Users Enabled'; ActiveName = 'M365 Copilot Users Active'; ActionsName = 'M365 Copilot Actions Taken' }
        )
        foreach ($sp in $specs) {
            $usersPeak = 0.0; $actionsPeak = 0.0
            foreach ($r in $Weeks.Rows) {
                $usersPeak = [math]::Max($usersPeak, [double] $r["$($sp.Prefix)Active"])
                if (-not ($r["$($sp.Prefix)Enabled"] -is [DBNull])) { $usersPeak = [math]::Max($usersPeak, [double] $r["$($sp.Prefix)Enabled"]) }
                $actionsPeak = [math]::Max($actionsPeak, [double] $r["$($sp.Prefix)Actions"])
            }
            $ch = $ws.Drawings.AddChart($sp.Name, [OfficeOpenXml.Drawing.Chart.eChartType]::LineMarkers)
            $ch.SetPosition($sp.Top - 1, 0, 1, 0)
            $ch.SetSize(900, 400)
            $ch.ChartXml.LoadXml((New-ComboChartXml -Title $sp.Title -Sheet 'Weekly' -Categories "A2:A$lastRow" `
                -Enabled $sp.Enabled -EnabledName $sp.EnabledName -Active $sp.Active -ActiveName $sp.ActiveName `
                -Actions $sp.Actions -ActionsName $sp.ActionsName `
                -UsersMax (Get-NiceMax ($usersPeak * 1.12)) -ActionsMax (Get-NiceMax ($actionsPeak * 1.55))))
        }

        # ---- Notes -------------------------------------------------------------
        $groupsDay = if ($Meta.GroupsCounted -is [DBNull]) { 'not counted this week' } else { 'counted on ' + ([datetime] $Meta.GroupsCounted).ToString('M/d/yyyy', [Globalization.CultureInfo]::InvariantCulture) }
        $members = if (Test-GroupCountAll $Meta) { 'Every member, disabled accounts included,' } else { 'The members whose AD account is enabled' }
        $countAll = Test-GroupCountAll $Meta
        $whoCounted = if ($countAll) { 'This report counts everyone: Users Enabled every member of the groups, and Active Users everyone who used Copilot, including accounts since disabled and people who have left (the dashboard setting, or -IncludeDisabled). Actions taken and Security Alerts count all use.' }
                      else { 'Users Enabled and Active Users count only accounts enabled today: accounts since disabled and people who have left are not among them. Actions taken and Security Alerts count all use, theirs included. To count everyone, set the dashboard (Admin > Alerts and digests) or run with -IncludeDisabled.' }
        $activeText = if ($countAll) { 'Everyone who used Copilot in the week' } else { 'People whose AD account is enabled today who used Copilot in the week' }
        $notes = @(
            @('Week', "Saturday 00:00 to Friday 23:59:59.9999 in the reporting time zone ($($Meta.ReportingTimeZone)): the weeks the dashboard and Power BI model use."),
            @('M365 Copilot', 'Use made with a Microsoft 365 Copilot license at the time of use.'),
            @('Copilot Chat', 'Use made without a Copilot license (including use the license history could not place).'),
            @('Who is counted', $whoCounted),
            @('Users Enabled', "$members of each column's AD group (named on the dashboard, Admin > Directory quality, or in copilot-audit.settings.psd1), nested groups included, as the nightly AD load counted them on the last day it ran in the week ($groupsDay). Blank for a week before the groups were counted."),
            @('Active Users', "$activeText, with a license (M365 Copilot) or without one (Copilot Chat). Someone licensed part-way through a week can be in both."),
            @('Actions taken', 'Interactions: one for each Copilot audit record, a prompt and its answer. Not sessions.'),
            @('Security Alerts', 'Interactions with a jailbreak attempt, a prompt injection in content Copilot read, or a blocked resource.'),
            @('Data through', "$through. Data is complete when it reaches the Monday after the week: $([bool] $Meta.DataComplete)."),
            @('Source', 'bi.usp_WeeklyCopilotReport in the Copilot database.')
        )
        $wn.Column(1).Width = 18; $wn.Column(2).Width = 110
        $wn.Cells['A1'].Value = 'How the figures are counted'
        $wn.Cells['A1'].Style.Font.Bold = $true; $wn.Cells['A1'].Style.Font.Size = 13
        $row = 3
        foreach ($n in $notes) {
            $wn.Cells[$row, 1].Value = $n[0]; $wn.Cells[$row, 1].Style.Font.Bold = $true
            $wn.Cells[$row, 2].Value = $n[1]; $wn.Cells[$row, 2].Style.WrapText = $true
            $row++
        }

        # ---- Stories: every approved story to date, as the email quotes them ---
        if ($Library.Count) {
            $st = $pkg.Workbook.Worksheets.Add('Stories')
            $st.Cells.Style.Font.Name = 'Segoe UI'; $st.Cells.Style.Font.Size = 10
            $heads = 'Story', 'Who', 'App or agent', 'Kind of work', 'Minutes saved a week', 'Sent'
            for ($c = 0; $c -lt $heads.Count; $c++) {
                $cell = $st.Cells[1, $c + 1]
                $cell.Value = $heads[$c]; $cell.Style.Font.Bold = $true
                $cell.Style.Fill.PatternType = [OfficeOpenXml.Style.ExcelFillStyle]::Solid
                $cell.Style.Fill.BackgroundColor.SetColor((& $color $p.SubHead))
            }
            $row = 2
            foreach ($s in $Library) {
                $st.Cells[$row, 1].Value = $s.Quote; $st.Cells[$row, 1].Style.WrapText = $true
                $st.Cells[$row, 2].Value = $s.Who
                $st.Cells[$row, 3].Value = $s.App
                $st.Cells[$row, 4].Value = $s.UseCase
                if ($null -ne $s.MinutesSaved) { $st.Cells[$row, 5].Value = $s.MinutesSaved }
                $st.Cells[$row, 6].Value = if ($s.Sends) { $s.Sends } else { 'Not yet' }
                $row++
            }
            $st.Column(1).Width = 80; $st.Column(2).Width = 30; $st.Column(3).Width = 18; $st.Column(4).Width = 20; $st.Column(5).Width = 12; $st.Column(6).Width = 24
            $st.View.FreezePanes(2, 1)
        }

        $ws.PrinterSettings.Orientation = [OfficeOpenXml.eOrientation]::Landscape
        $ws.PrinterSettings.FitToPage = $true
        $ws.PrinterSettings.FitToWidth = 1
        $ws.PrinterSettings.FitToHeight = 0
        $ws.Select()
        $pkg.SaveAs((New-Object IO.FileInfo($Path)))
    }
    finally { $pkg.Dispose() }
}

# ===========================================================================
# Sending
# ===========================================================================

function Get-DashboardMail {
    <#
        How to send, as the dashboard sends (Admin > Email): from dash.Setting, and the password from dash.Secret
        through dash.usp_GetSecret. Follows the dashboard's own rules (Infrastructure/Mail.cs, MailConfig): Exchange
        Online is smtp.office365.com:587 with STARTTLS, signing in as the user name or else the From address; a
        stored password is used only while the mode, server, port, security, certificate check and user name are
        those it was typed in with. Throws, in words an administrator can act on, what stops it sending.
    #>
    param($Connection)
    $hasDash = Invoke-ReportQuery $Connection "SELECT CASE WHEN OBJECT_ID(N'dash.Setting', N'U') IS NULL OR OBJECT_ID(N'dash.usp_GetSecret', N'P') IS NULL THEN 0 ELSE 1 END AS Has;"
    if (-not [int] $hasDash.Tables[0].Rows[0].Has) {
        throw 'The weekly report sends through the mail server set in the dashboard (Admin > Email), and this database has no dashboard settings: install with Install-CopilotAudit.ps1 -Dashboard and set up email there.'
    }
    $rows = (Invoke-ReportQuery $Connection "SELECT Name, Value FROM dash.Setting WHERE Name IN ('MailMode', 'MailFrom', 'MailReplyTo', 'SmtpHost', 'SmtpPort', 'SmtpSecurity', 'SmtpUser', 'SmtpAcceptAnyCertificate', 'ExoAuth');").Tables[0]
    $v = @{}
    foreach ($r in $rows.Rows) { if (-not ($r.Value -is [DBNull]) -and ([string] $r.Value).Trim()) { $v[[string] $r.Name] = ([string] $r.Value).Trim() } }
    $get = { param([string] $k) if ($v.ContainsKey($k)) { $v[$k] } else { $null } }

    $mode = 'Off'
    foreach ($m in 'Off', 'ExchangeOnline', 'Smtp') { if ((& $get 'MailMode') -and $m -eq (& $get 'MailMode')) { $mode = $m } }
    if ($mode -eq 'Off') { throw 'Email is off in the dashboard (Admin > Email): turn it on there; the weekly report sends through the same server.' }
    $from = & $get 'MailFrom'
    if (-not $from) { throw 'Set the From address in the dashboard (Admin > Email).' }
    $exoAuth = if ((& $get 'ExoAuth') -eq 'OAuth') { 'OAuth' } else { 'Password' }
    if ($mode -eq 'ExchangeOnline' -and $exoAuth -eq 'OAuth') {
        throw 'Admin > Email signs in to Exchange Online with OAuth, which the weekly report cannot do: it needs the SMTP server mode, or Exchange Online with a password.'
    }
    $acceptAny = (& $get 'SmtpAcceptAnyCertificate') -eq '1'

    if ($mode -eq 'ExchangeOnline') { $hostName = 'smtp.office365.com'; $port = 587; $tls = 'StartTls'; $user = & $get 'SmtpUser'; if (-not $user) { $user = $from } }
    else {
        $hostName = & $get 'SmtpHost'
        if (-not $hostName) { throw 'Set the SMTP server in the dashboard (Admin > Email).' }
        $port = 587; $parsed = 0
        if ([int]::TryParse([string] (& $get 'SmtpPort'), [ref] $parsed)) { $port = $parsed }
        if ($port -lt 1 -or $port -gt 65535) { throw 'The SMTP port in Admin > Email is not 1 to 65535.' }
        $security = 'StartTls'
        foreach ($c in 'StartTls', 'Implicit', 'Auto', 'None') { if ((& $get 'SmtpSecurity') -and $c -eq (& $get 'SmtpSecurity')) { $security = $c } }
        # The names the dashboard's PasswordTarget writes (MailKit's SecureSocketOptions).
        $tls = switch ($security) { 'Implicit' { 'SslOnConnect' } 'Auto' { 'Auto' } 'None' { 'None' } default { 'StartTls' } }
        $user = & $get 'SmtpUser'
    }
    # The .NET mail client the report uses speaks STARTTLS or plain text, not TLS from the first byte.
    if ($tls -eq 'SslOnConnect' -or ($tls -eq 'Auto' -and $port -eq 465)) {
        throw "Admin > Email uses TLS from the first byte (Implicit, port $port), which the weekly report cannot send with: choose StartTls (port 587 or 25) there if the server offers it."
    }

    $password = $null
    if ($user) {
        $password = Get-DashboardSecret -Connection $Connection -Name 'SmtpPassword'
        if (-not $password) { throw 'A user name is set in Admin > Email but no password is stored: type it there.' }
        $target = '{0}|{1}|{2}|{3}|{4}|{5}' -f $mode, $hostName.ToLowerInvariant(), $port, $tls, $user.ToLowerInvariant(), $(if ($acceptAny) { 'any certificate' } else { 'checked' })
        $savedFor = Get-DashboardSecret -Connection $Connection -Name 'SmtpPasswordFor'
        if ($savedFor -and $savedFor -ne $target) {
            throw 'The server, port, security or user name changed after the password was stored: type the password again in Admin > Email.'
        }
    }
    @{ Mode = $mode; Host = $hostName; Port = $port; Ssl = ($tls -ne 'None'); User = $user; Password = $password
       AcceptAnyCertificate = $acceptAny; From = $from; ReplyTo = (& $get 'MailReplyTo') }
}

function Get-DashboardSecret {
    <# One secret the dashboard stored (dash.Secret), decrypted by its own procedure; $null when there is none. #>
    param($Connection, [string] $Name)
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = 'dash.usp_GetSecret'
    $cmd.CommandType = [System.Data.CommandType]::StoredProcedure
    [void] $cmd.Parameters.Add('@Name', [System.Data.SqlDbType]::VarChar, 64)
    $cmd.Parameters['@Name'].Value = $Name
    $out = $cmd.Parameters.Add('@Value', [System.Data.SqlDbType]::NVarChar, 2000)
    $out.Direction = [System.Data.ParameterDirection]::Output
    [void] $cmd.ExecuteNonQuery()
    if ($out.Value -is [DBNull]) { $null } else { [string] $out.Value }
}

function Get-AcceptAnyCertificateCallback {
    <# A certificate check that accepts any certificate, for a mail server whose certificate Admin > Email says not to check. #>
    if (-not ('CopilotWeeklyReportTls' -as [type])) {
        Add-Type -TypeDefinition @'
public static class CopilotWeeklyReportTls
{
    static bool Any(object sender, System.Security.Cryptography.X509Certificates.X509Certificate certificate,
                    System.Security.Cryptography.X509Certificates.X509Chain chain, System.Net.Security.SslPolicyErrors errors) { return true; }
    public static System.Net.Security.RemoteCertificateValidationCallback Callback { get { return Any; } }
}
'@
    }
    [CopilotWeeklyReportTls]::Callback
}

function Send-ReportMail {
    param([hashtable] $Mail, [string] $Subject, [string] $Html, [string] $Text,
          [hashtable] $ImageFiles, [string] $Workbook, [string[]] $ToList, [string[]] $CcList, [string[]] $BccList)
    $msg = New-Object System.Net.Mail.MailMessage
    $disposables = New-Object System.Collections.Generic.List[IDisposable]
    try {
        $msg.From = New-Object System.Net.Mail.MailAddress($Mail.From)
        if ($Mail.ReplyTo) { $msg.ReplyToList.Add($Mail.ReplyTo) }
        foreach ($a in $ToList) { if ($a) { $msg.To.Add($a) } }
        foreach ($a in $CcList) { if ($a) { $msg.CC.Add($a) } }
        foreach ($a in $BccList) { if ($a) { $msg.Bcc.Add($a) } }
        if (($msg.To.Count + $msg.CC.Count + $msg.Bcc.Count) -eq 0) { throw 'No recipients.' }
        $msg.Subject = $Subject
        $msg.SubjectEncoding = [Text.Encoding]::UTF8
        $msg.HeadersEncoding = [Text.Encoding]::UTF8

        $plain = [System.Net.Mail.AlternateView]::CreateAlternateViewFromString($Text, [Text.Encoding]::UTF8, 'text/plain')
        $htmlView = [System.Net.Mail.AlternateView]::CreateAlternateViewFromString($Html, [Text.Encoding]::UTF8, 'text/html')
        foreach ($k in $ImageFiles.Keys) {
            $lr = New-Object System.Net.Mail.LinkedResource($ImageFiles[$k], 'image/png')
            $lr.ContentId = $k
            $lr.TransferEncoding = [System.Net.Mime.TransferEncoding]::Base64
            $htmlView.LinkedResources.Add($lr)
        }
        $msg.AlternateViews.Add($plain)
        $msg.AlternateViews.Add($htmlView)
        if ($Workbook) {
            $att = New-Object System.Net.Mail.Attachment($Workbook, 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet')
            $att.Name = [IO.Path]::GetFileName($Workbook)
            $msg.Attachments.Add($att)
        }

        $smtp = New-Object System.Net.Mail.SmtpClient($Mail.Host, [int] $Mail.Port)
        $disposables.Add($smtp)
        $smtp.EnableSsl = [bool] $Mail.Ssl
        $smtp.UseDefaultCredentials = $false
        if ($Mail.User) { $smtp.Credentials = New-Object System.Net.NetworkCredential($Mail.User, $Mail.Password) }
        $smtp.Timeout = 120000
        # The .NET Framework's mail client checks certificates through ServicePointManager: replaced for this send
        # alone, when Admin > Email says not to check (a relay whose certificate does not match its name, say).
        $previousCheck = [System.Net.ServicePointManager]::ServerCertificateValidationCallback
        if ($Mail.AcceptAnyCertificate) { [System.Net.ServicePointManager]::ServerCertificateValidationCallback = Get-AcceptAnyCertificateCallback }
        try { $smtp.Send($msg) }
        finally { [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $previousCheck }
    }
    finally {
        $msg.Dispose()
        foreach ($d in $disposables) { $d.Dispose() }
    }
}

# ===========================================================================
# Main
# ===========================================================================

$setting = Get-ReportSetting -Path $SettingsPath
$logDir = Join-Path $script:Root 'logs'
if (-not (Test-Path -LiteralPath $logDir)) { [void] (New-Item -ItemType Directory -Path $logDir) }
$script:LogPath = Join-Path $logDir 'weekly-report.log'

$conn = Open-ReportConnection -Setting $setting
try {
    # ---- which week, and is it due --------------------------------------------
    $zoneId = [string] (Invoke-ReportQuery $conn 'SELECT audit.ReportingTimeZone() AS Zone;').Tables[0].Rows[0].Zone
    $tz = [TimeZoneInfo]::FindSystemTimeZoneById($zoneId)
    $nowLocal = [TimeZoneInfo]::ConvertTimeFromUtc([DateTime]::UtcNow, $tz)

    if ($PSBoundParameters.ContainsKey('WeekEnding')) {
        $week = $WeekEnding.Date
        if ($week.DayOfWeek -ne [DayOfWeek]::Friday) { throw "-WeekEnding must be a Friday; $($week.ToString('yyyy-MM-dd')) is a $($week.DayOfWeek)." }
    }
    else {
        $week = $nowLocal.Date.AddDays(-1)
        while ($week.DayOfWeek -ne [DayOfWeek]::Friday) { $week = $week.AddDays(-1) }
    }

    $sendDay = [DayOfWeek] $setting.SendDay
    $offset = (([int] $sendDay - [int] [DayOfWeek]::Friday) + 7) % 7
    if ($offset -eq 0) { $offset = 7 }
    $due = $week.AddDays($offset).Add([TimeSpan]::Parse($setting.SendTime))

    $record = $null
    if ($Scheduled) {
        # The day before the send, from the send time: the success stories reminder, once a week.
        if ($nowLocal -lt $due -and $nowLocal -ge $due.AddDays(-1)) { Send-StoryReminder -Connection $conn -Week $week }
        if ($nowLocal -lt $due) { exit 0 }   # not yet: quiet, the task runs every hour
        $record = Get-SendRecord -Connection $conn -Week $week
        if ($record -and $record.Status -eq 'Sent') { exit 0 }
        if ($record -and [int] $record.Attempts -ge [int] $setting.MaxAttempts) { exit 0 }
    }
    elseif (-not $Preview -and -not $To -and -not $Force) {
        $record = Get-SendRecord -Connection $conn -Week $week
        if ($record -and $record.Status -eq 'Sent') {
            Write-ReportLog "The week ending $($week.ToString('yyyy-MM-dd')) was already sent ($($record.SentUtc) UTC). -Force sends it again."
            exit 0
        }
    }

    # ---- the figures -----------------------------------------------------------
    # The people rows follow the dashboard's setting unless -IncludeDisabled asks for everyone, for this run only.
    $peopleCount = if ($IncludeDisabled) { 'All' } else { $null }
    if ($IncludeDisabled) { Write-ReportLog 'This run counts disabled accounts and people who have left in Users Enabled and Active Users (-IncludeDisabled).' }
    $ds = Invoke-ReportQuery $conn 'EXEC bi.usp_WeeklyCopilotReport @WeekEnding = @w, @Weeks = @n, @GroupCount = @g;' @{
        '@w' = $week; '@n' = [int] $setting.Weeks; '@g' = $peopleCount }
    $meta = $ds.Tables[0].Rows[0]
    $table = $ds.Tables[1]
    $weeks = $ds.Tables[2]
    $complete = [bool] $meta.DataComplete

    $warnings = New-Object System.Collections.Generic.List[string]
    if ($script:UnusedMailSettings.Count) {
        Write-ReportLog "Not read from 3.1.3, and safe to delete from the settings file: $($script:UnusedMailSettings -join ', '). The mail server and From address come from the dashboard (Admin > Email), and Users Enabled from the AD groups." 'WARN'
    }
    if (-not $complete) {
        $through = ([datetime] $meta.DataThrough).ToString('M/d/yyyy', [Globalization.CultureInfo]::InvariantCulture)
        if ($Scheduled -and $nowLocal -lt $due.AddHours([double] $setting.MaxWaitHours)) {
            Write-ReportLog "Week ending $($week.ToString('yyyy-MM-dd')): data reaches $through, not yet the Monday after the week. Waiting (up to $($setting.MaxWaitHours) hours after the send time)."
            exit 0
        }
        $warnings.Add("The data reaches $through, not yet the Monday after the week: records Purview delivers late may be missing, so the last days may be low. Check that the nightly run is succeeding.")
    }

    # ---- build -----------------------------------------------------------------
    $stamp = $week.ToString('yyyy-MM-dd')
    if ($Preview) {
        $outDir = if ($OutputFolder) { $OutputFolder } else { Join-Path $script:Root (Join-Path 'reports' 'preview') }
    }
    else {
        $outDir = Join-Path $script:Root (Join-Path 'reports' $stamp)
    }
    if (-not (Test-Path -LiteralPath $outDir)) { [void] (New-Item -ItemType Directory -Path $outDir -Force) }

    $images = @{}
    $chatPng = Join-Path $outDir "copilot-chat-$stamp.png"
    $m365Png = Join-Path $outDir "m365-copilot-$stamp.png"
    try {
        New-WeeklyChartPng -Title 'Copilot Chat Enabled vs. Active vs. Actions Taken' -Weeks $weeks -Prefix 'Chat' `
            -EnabledName 'Chat Users Enabled' -ActiveName 'Chat Users Active' -ActionsName 'Chat Actions Taken' -Path $chatPng
        New-WeeklyChartPng -Title 'M365 Copilot Users Enabled vs. Active vs. Actions Taken' -Weeks $weeks -Prefix 'M365' `
            -EnabledName 'M365 Copilot Users Enabled' -ActiveName 'M365 Copilot Users Active' -ActionsName 'M365 Copilot Actions Taken' -Path $m365Png
        $images['Chat'] = $chatPng
        $images['M365'] = $m365Png
    }
    catch {
        Write-ReportLog "Charts not drawn, the email goes without them: $($_.Exception.Message)" 'WARN'
        $images = @{}
    }

    # In their words: the stories ticked on the dashboard (none before 3.1.6, or when StoriesPerReport is 0).
    $stories = @(); $library = @()
    try {
        $stories = @(Get-ReportStory -Connection $conn)
        $library = @(Get-ReportStory -Connection $conn -All)
        if ($stories.Count) { Write-ReportLog "In their words: $($stories.Count) success $(if ($stories.Count -eq 1) { 'story' } else { 'stories' }) (ids $(($stories | ForEach-Object { $_.StoryId }) -join ', '))." }
    }
    catch {
        Write-ReportLog "Success stories not read, the email goes without them: $($_.Exception.Message)" 'WARN'
        $stories = @(); $library = @()
    }

    $workbook = Join-Path $outDir "Copilot weekly report $stamp.xlsx"
    try { New-ReportWorkbook -Meta $meta -Table $table -Weeks $weeks -Path $workbook -Library $library }
    catch {
        Write-ReportLog "Workbook not written, the email goes without it: $($_.Exception.Message)" 'WARN'
        $workbook = $null
    }

    # Money found, from the saving ledger: never stops the email.
    $saving = $null
    try { $saving = Get-ReportSaving -Connection $conn; if ($saving) { Write-ReportLog "Money found: $saving" } }
    catch { Write-ReportLog "Saving ledger not read, the email goes without its money line: $($_.Exception.Message)" 'WARN' }

    $subject = "$($setting.SubjectPrefix): week of $($meta.WeekLabel)"
    $text = New-ReportText -Meta $meta -Table $table -Weeks $weeks -Stories $stories -Saving $saving

    if ($Preview) {
        $fileRefs = @{}
        foreach ($k in $images.Keys) { $fileRefs[$k] = [IO.Path]::GetFileName($images[$k]) }
        $htmlPath = Join-Path $outDir "Copilot weekly report $stamp.html"
        $html = New-ReportHtml -Meta $meta -Table $table -Images $fileRefs -Warnings $warnings -Weeks $weeks -Stories $stories -Saving $saving
        [IO.File]::WriteAllText($htmlPath, $html, (New-Object Text.UTF8Encoding($false)))
        Write-ReportLog "Preview of the week ending ${stamp}: $htmlPath"
        if ($workbook) { Write-ReportLog "Workbook: $workbook" }
        Write-ReportLog "Subject would be: $subject"
        try { $previewMail = Get-DashboardMail $conn; Write-ReportLog "It would go from $($previewMail.From) through $($previewMail.Host):$($previewMail.Port) (Admin > Email)." }
        catch { Write-ReportLog "It could not be sent as things stand: $($_.Exception.Message)" 'WARN' }
        exit 0
    }

    $cids = @{}; $cidFiles = @{}
    foreach ($k in $images.Keys) { $cids[$k] = "cid:chart$k"; $cidFiles["chart$k"] = $images[$k] }
    $html = New-ReportHtml -Meta $meta -Table $table -Images $cids -Warnings $warnings -Weeks $weeks -Stories $stories -Saving $saving
    if ($setting.KeepCopies) {
        [IO.File]::WriteAllText((Join-Path $outDir "Copilot weekly report $stamp.html"), $html, (New-Object Text.UTF8Encoding($false)))
    }

    if ($To) { $toList = $To; $ccList = @(); $bccList = @() }
    else { $toList = @($setting.To); $ccList = @($setting.Cc); $bccList = @($setting.Bcc) }
    $who = (@($toList) + @($ccList) + @($bccList) | Where-Object { $_ }) -join '; '
    $keepRecord = -not $To

    try {
        $mail = Get-DashboardMail $conn
        Send-ReportMail -Mail $mail -Subject $subject -Html $html -Text $text -ImageFiles $cidFiles -Workbook $workbook `
            -ToList $toList -CcList $ccList -BccList $bccList
    }
    catch {
        $err = $_.Exception
        while ($err.InnerException) { $err = $err.InnerException }
        Write-ReportLog "Week ending ${stamp}: the email was not sent: $($err.Message)" 'ERROR'
        if ($keepRecord) { Set-SendRecord -Connection $conn -Week $week -Status 'Failed' -Recipients $who -DataComplete $complete -Detail $err.Message }
        exit 1
    }
    $note = if ($warnings.Count) { 'Sent before the data was complete.' } else { $null }
    if ($keepRecord) { Set-SendRecord -Connection $conn -Week $week -Status 'Sent' -Recipients $who -DataComplete $complete -Detail $note }
    if ($keepRecord -and $stories.Count) {
        try { Complete-ReportStory -Connection $conn -Stories $stories -Week $week }
        catch { Write-ReportLog "The stories quoted were not marked sent; untick them on the dashboard so they are not sent again: $($_.Exception.Message)" 'WARN' }
    }
    if (-not $setting.KeepCopies -and -not $To) { Remove-Item -LiteralPath $outDir -Recurse -Force -ErrorAction SilentlyContinue }
    Write-ReportLog "Week ending ${stamp}: sent to $who."
}
catch {
    Write-ReportLog "The weekly report failed: $($_.Exception.Message)" 'ERROR'
    exit 1
}
finally {
    if ($conn) { $conn.Dispose() }
}
