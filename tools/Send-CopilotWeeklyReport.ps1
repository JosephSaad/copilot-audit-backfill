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

.EXAMPLE
    powershell.exe -NoProfile -File .\tools\Send-CopilotWeeklyReport.ps1 -Preview

.EXAMPLE
    powershell.exe -NoProfile -File .\tools\Send-CopilotWeeklyReport.ps1 -To me@contoso.com
#>
[CmdletBinding()]
param(
    [string]   $SettingsPath,
    [datetime] $WeekEnding,
    [switch]   $Scheduled,
    [switch]   $Preview,
    [string[]] $To,
    [switch]   $Force,
    [string]   $OutputFolder
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
        SmtpServer = ''; SmtpPort = 25; SmtpUseSsl = $false; SmtpUseDefaultCredentials = $false
        From = ''; To = @(); Cc = @(); Bcc = @(); SubjectPrefix = 'Copilot weekly report'
        SendDay = 'Tuesday'; SendTime = '08:00'; MaxWaitHours = 24; MaxAttempts = 8
        Weeks = 13; ChatEnabled = 'AllUsers'; KeepCopies = $true
    }
    foreach ($k in @($s.Keys)) { $d[$k] = $s[$k] }

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
    if (@('AllUsers', 'Unlicensed') -notcontains $d.ChatEnabled) { throw "ChatEnabled must be AllUsers or Unlicensed, not '$($d.ChatEnabled)'." }
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
    HeadAll   = '#16465E'   # all-time header
    SubHead   = '#BDD7EE'
    SubHeadAll= '#D6E4F0'
    RowA      = '#DDEBF7'
    RowB      = '#CFE2F3'
    RowAllA   = '#F2F7FC'
    RowAllB   = '#E8F0F8'
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
        $enabledMax = [math]::Max($enabledMax, [double] $r["${Prefix}Enabled"])
        $activeMax = [math]::Max($activeMax, [double] $r["${Prefix}Active"])
        $actionsMax = [math]::Max($actionsMax, [double] $r["${Prefix}Prompts"])
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
        [void] $sArea.Points.AddXY($x, [object[]] @([double] $r["${Prefix}Prompts"]))
        [void] $sEnabled.Points.AddXY($x, [object[]] @([double] $r["${Prefix}Enabled"]))
        [void] $sActive.Points.AddXY($x, [object[]] @([double] $r["${Prefix}Active"]))
    }
    # Only the last week's value is bold and larger: the one the table reports.
    foreach ($s in $sArea, $sEnabled, $sActive) {
        $last = $s.Points[$s.Points.Count - 1]
        $last.Font = & $font 12 ([Drawing.FontStyle]::Bold)
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

function New-ReportHtml {
    param($Meta, [System.Data.DataTable] $Table, [hashtable] $Images, [string[]] $Warnings)
    $p = $script:Palette
    $f = "font-family:'Segoe UI',Arial,sans-serif;"
    $weekLabel = ConvertTo-Html5Text ([string] $Meta.WeekLabel)
    $from = ([datetime] $Meta.AllTimeFrom).ToString('M/d/yyyy', [Globalization.CultureInfo]::InvariantCulture)
    $through = ([datetime] $Meta.DataThrough).ToString('M/d/yyyy', [Globalization.CultureInfo]::InvariantCulture)
    $zone = ConvertTo-Html5Text ([string] $Meta.ReportingTimeZone)

    $sb = New-Object System.Text.StringBuilder
    [void] $sb.Append("<!DOCTYPE html><html><head><meta charset=`"utf-8`"></head><body style=`"margin:0;padding:0;background:#ffffff;`">")
    [void] $sb.Append("<table role=`"presentation`" cellpadding=`"0`" cellspacing=`"0`" border=`"0`" width=`"920`" style=`"$f color:$($p.Text);`"><tr><td style=`"padding:16px 10px;`">")
    [void] $sb.Append("<div style=`"$f font-size:20px;font-weight:700;color:$($p.Navy);`">Weekly vs. All Time: Copilot Chat and M365 Copilot Adoption and Security</div>")
    [void] $sb.Append("<div style=`"$f font-size:13px;color:$($p.Muted);padding:4px 0 14px 0;`">Week of $weekLabel (Saturday to Friday, $zone). All time since $from. Data through $through.</div>")

    foreach ($w in $Warnings) {
        [void] $sb.Append("<div style=`"$f font-size:13px;background:#FFF4CE;border-left:4px solid #E3A400;padding:8px 12px;margin:0 0 12px 0;`">$(ConvertTo-Html5Text $w)</div>")
    }

    $th = "$f font-size:14px;font-weight:700;padding:9px 12px;text-align:center;border:1px solid #ffffff;"
    $td = "$f font-size:15px;padding:9px 12px;text-align:center;border:1px solid #ffffff;"
    [void] $sb.Append("<table cellpadding=`"0`" cellspacing=`"0`" border=`"0`" style=`"border-collapse:collapse;width:900px;`">")
    [void] $sb.Append("<tr><td rowspan=`"2`" style=`"$th background:$($p.HeadDark);width:250px;`"></td>")
    [void] $sb.Append("<td colspan=`"2`" style=`"$th background:$($p.HeadDark);color:#ffffff;font-size:16px;`">Week of $weekLabel</td>")
    [void] $sb.Append("<td colspan=`"2`" style=`"$th background:$($p.HeadAll);color:#ffffff;font-size:16px;`">All time since $from</td></tr>")
    [void] $sb.Append("<tr><td style=`"$th background:$($p.SubHead);color:$($p.Text);`">M365 Copilot</td><td style=`"$th background:$($p.SubHead);color:$($p.Text);`">Copilot Chat</td>")
    [void] $sb.Append("<td style=`"$th background:$($p.SubHeadAll);color:$($p.Text);`">M365 Copilot</td><td style=`"$th background:$($p.SubHeadAll);color:$($p.Text);`">Copilot Chat</td></tr>")
    $i = 0
    foreach ($r in $Table.Rows) {
        $bg = if ($i % 2 -eq 0) { $p.RowA } else { $p.RowB }
        $bgAll = if ($i % 2 -eq 0) { $p.RowAllA } else { $p.RowAllB }
        [void] $sb.Append("<tr><td style=`"$td background:$($p.Label);color:#ffffff;text-align:left;font-size:15px;`">$(ConvertTo-Html5Text ([string] $r.Metric))</td>")
        [void] $sb.Append("<td style=`"$td background:$bg;font-weight:600;`">$(Format-Count $r.WeekM365)</td><td style=`"$td background:$bg;font-weight:600;`">$(Format-Count $r.WeekChat)</td>")
        [void] $sb.Append("<td style=`"$td background:$bgAll;`">$(Format-Count $r.AllTimeM365)</td><td style=`"$td background:$bgAll;`">$(Format-Count $r.AllTimeChat)</td></tr>")
        $i++
    }
    [void] $sb.Append('</table>')

    foreach ($key in 'Chat', 'M365') {
        if ($Images -and $Images.ContainsKey($key)) {
            [void] $sb.Append("<div style=`"padding:22px 0 0 0;`"><img src=`"$($Images[$key])`" width=`"900`" height=`"380`" alt=`"$key chart`" style=`"display:block;border:1px solid #E3E8EE;`"></div>")
        }
    }

    $n = "$f font-size:12px;color:$($p.Muted);line-height:1.5;"
    [void] $sb.Append("<div style=`"$n padding:18px 0 0 0;`"><b>How the figures are counted.</b> ")
    [void] $sb.Append('<b>M365 Copilot</b> is use made with a Microsoft 365 Copilot license; <b>Copilot Chat</b> is use made without one. ')
    [void] $sb.Append('<b>Users enabled</b>: M365 Copilot, people holding a Copilot license on the week''s last day (all time: everyone who held one); ')
    if ($Meta.ChatEnabledScope -eq 'Unlicensed') { [void] $sb.Append('Copilot Chat, people seen using Copilot without a license since the data starts. ') }
    else { [void] $sb.Append('Copilot Chat, people seen in the Copilot audit log since the data starts. ') }
    [void] $sb.Append('<b>Active users</b>: people who used Copilot in the week (M365 Copilot: with a license they still held at the week''s end). ')
    [void] $sb.Append('<b>Actions taken</b>: prompts submitted. <b>Security alerts</b>: interactions with a jailbreak attempt, a prompt injection or a blocked resource.')
    [void] $sb.Append(' An Excel workbook with these figures and the charts is attached.</div>')
    [void] $sb.Append('</td></tr></table></body></html>')
    $sb.ToString()
}

function New-ReportText {
    param($Meta, [System.Data.DataTable] $Table)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("Copilot weekly report, week of $($Meta.WeekLabel)")
    $lines.Add('')
    $lines.Add(('{0,-26}{1,14}{2,14}{3,16}{4,16}' -f '', 'M365 Copilot', 'Copilot Chat', 'M365 (all time)', 'Chat (all time)'))
    foreach ($r in $Table.Rows) {
        $lines.Add(('{0,-26}{1,14}{2,14}{3,16}{4,16}' -f $r.Metric, (Format-Count $r.WeekM365), (Format-Count $r.WeekChat), (Format-Count $r.AllTimeM365), (Format-Count $r.AllTimeChat)))
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
    param($Meta, [System.Data.DataTable] $Table, [System.Data.DataTable] $Weeks, [string] $Path)
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
                 'Copilot Chat users enabled', 'Copilot Chat active users', 'Copilot Chat actions taken', 'Copilot Chat security alerts'
        $cols = 'WeekLabel', 'WeekStart', 'WeekEnding', 'M365Enabled', 'M365Active', 'M365Prompts', 'M365Signals',
                'ChatEnabled', 'ChatActive', 'ChatPrompts', 'ChatSignals'
        for ($c = 0; $c -lt $heads.Count; $c++) { $wd.Cells[1, ($c + 1)].Value = $heads[$c] }
        $row = 2
        foreach ($r in $Weeks.Rows) {
            for ($c = 0; $c -lt $cols.Count; $c++) {
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
        $tbl = $wd.Tables.Add($wd.Cells["A1:K$lastRow"], 'WeeklyFigures')
        $tbl.TableStyle = [OfficeOpenXml.Table.TableStyles]::Medium2
        $wd.Column(1).Width = 14; $wd.Column(2).Width = 12; $wd.Column(3).Width = 12
        for ($c = 4; $c -le 11; $c++) { $wd.Column($c).Width = 17 }
        $wd.Row(1).Height = 32
        $wd.Cells['A1:K1'].Style.WrapText = $true
        $wd.View.FreezePanes(2, 2)

        # ---- Summary: the table, as in the email -----------------------------
        $ws.View.ShowGridLines = $false
        $ws.Column(1).Width = 2
        $ws.Column(2).Width = 30
        for ($c = 3; $c -le 6; $c++) { $ws.Column($c).Width = 17 }
        $from = ([datetime] $Meta.AllTimeFrom).ToString('M/d/yyyy', [Globalization.CultureInfo]::InvariantCulture)
        $through = ([datetime] $Meta.DataThrough).ToString('M/d/yyyy', [Globalization.CultureInfo]::InvariantCulture)
        $ws.Cells['B2'].Value = 'Weekly vs. All Time: Copilot Chat and M365 Copilot Adoption and Security'
        $ws.Cells['B2'].Style.Font.Size = 15
        $ws.Cells['B2'].Style.Font.Bold = $true
        $ws.Cells['B2'].Style.Font.Color.SetColor((& $color $p.Navy))
        $ws.Cells['B3'].Value = "Week of $($Meta.WeekLabel) (Saturday to Friday, $($Meta.ReportingTimeZone)). All time since $from. Data through $through."
        $ws.Cells['B3'].Style.Font.Color.SetColor((& $color $p.Muted))

        $ws.Cells['C5:D5'].Merge = $true; $ws.Cells['C5'].Value = "Week of $($Meta.WeekLabel)"
        $ws.Cells['E5:F5'].Merge = $true; $ws.Cells['E5'].Value = "All time since $from"
        $ws.Cells['B5:B6'].Merge = $true
        $ws.Cells['C6'].Value = 'M365 Copilot'; $ws.Cells['D6'].Value = 'Copilot Chat'
        $ws.Cells['E6'].Value = 'M365 Copilot'; $ws.Cells['F6'].Value = 'Copilot Chat'
        $fill = {
            param($range, [string] $hex, [string] $fontHex, [bool] $bold)
            $range.Style.Fill.PatternType = [OfficeOpenXml.Style.ExcelFillStyle]::Solid
            $range.Style.Fill.BackgroundColor.SetColor((& $color $hex))
            $range.Style.Font.Color.SetColor((& $color $fontHex))
            $range.Style.Font.Bold = $bold
        }
        & $fill $ws.Cells['B5:D5'] $p.HeadDark '#FFFFFF' $true
        & $fill $ws.Cells['B6'] $p.HeadDark '#FFFFFF' $true
        & $fill $ws.Cells['E5:F5'] $p.HeadAll '#FFFFFF' $true
        & $fill $ws.Cells['C6:D6'] $p.SubHead $p.Text $true
        & $fill $ws.Cells['E6:F6'] $p.SubHeadAll $p.Text $true
        $ws.Cells['C5:F5'].Style.Font.Size = 12
        $row = 7; $i = 0
        foreach ($r in $Table.Rows) {
            $ws.Cells[$row, 2].Value = [string] $r.Metric
            $ws.Cells[$row, 3].Value = [double] $r.WeekM365
            $ws.Cells[$row, 4].Value = [double] $r.WeekChat
            $ws.Cells[$row, 5].Value = [double] $r.AllTimeM365
            $ws.Cells[$row, 6].Value = [double] $r.AllTimeChat
            $bg = if ($i % 2 -eq 0) { $p.RowA } else { $p.RowB }
            $bgAll = if ($i % 2 -eq 0) { $p.RowAllA } else { $p.RowAllB }
            & $fill $ws.Cells[$row, 2] $p.Label '#FFFFFF' $false
            $ws.Cells[$row, 2].Style.Indent = 1
            & $fill $ws.Cells[$row, 3, $row, 4] $bg $p.Text $true
            & $fill $ws.Cells[$row, 5, $row, 6] $bgAll $p.Text $false
            $row++; $i++
        }
        $ws.Cells['C7:F10'].Style.Numberformat.Format = '#,##0'
        $ws.Cells['C5:F10'].Style.HorizontalAlignment = [OfficeOpenXml.Style.ExcelHorizontalAlignment]::Center
        $ws.Cells['B5:F10'].Style.VerticalAlignment = [OfficeOpenXml.Style.ExcelVerticalAlignment]::Center
        $ws.Cells['B7:F10'].Style.Font.Size = 12
        for ($rr = 5; $rr -le 10; $rr++) { $ws.Row($rr).Height = 24 }
        $border = $ws.Cells['B5:F10'].Style.Border
        foreach ($b in $border.Top, $border.Bottom, $border.Left, $border.Right) {
            $b.Style = [OfficeOpenXml.Style.ExcelBorderStyle]::Thin
            $b.Color.SetColor([Drawing.Color]::White)
        }

        # ---- the two charts: native Excel charts over the Weekly sheet -------
        $specs = @(
            @{ Name = 'ChatChart'; Prefix = 'Chat'; Title = 'Copilot Chat Enabled vs. Active vs. Actions Taken'; Top = 12
               Enabled = "H2:H$lastRow"; Active = "I2:I$lastRow"; Actions = "J2:J$lastRow"
               EnabledName = 'Chat Users Enabled'; ActiveName = 'Chat Users Active'; ActionsName = 'Chat Actions Taken' },
            @{ Name = 'M365Chart'; Prefix = 'M365'; Title = 'M365 Copilot Users Enabled vs. Active vs. Actions Taken'; Top = 35
               Enabled = "D2:D$lastRow"; Active = "E2:E$lastRow"; Actions = "F2:F$lastRow"
               EnabledName = 'M365 Copilot Users Enabled'; ActiveName = 'M365 Copilot Users Active'; ActionsName = 'M365 Copilot Actions Taken' }
        )
        foreach ($sp in $specs) {
            $usersPeak = 0.0; $actionsPeak = 0.0
            foreach ($r in $Weeks.Rows) {
                $usersPeak = [math]::Max($usersPeak, [math]::Max([double] $r["$($sp.Prefix)Enabled"], [double] $r["$($sp.Prefix)Active"]))
                $actionsPeak = [math]::Max($actionsPeak, [double] $r["$($sp.Prefix)Prompts"])
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
        $notes = @(
            @('Week', "Saturday 00:00 to Friday 23:59:59.9999 in the reporting time zone ($($Meta.ReportingTimeZone)): the weeks the dashboard and Power BI model use."),
            @('M365 Copilot', 'Use made with a Microsoft 365 Copilot license, at the time of use.'),
            @('Copilot Chat', 'Use made without a Copilot license (including use the license history could not place).'),
            @('Users enabled', 'M365 Copilot: people holding a Copilot license on the last day of the week with license data. All time: everyone who held one.'),
            @('', $(if ($Meta.ChatEnabledScope -eq 'Unlicensed') { 'Copilot Chat: people seen using Copilot without a license, from the first day of data to the week''s end.' } else { 'Copilot Chat: people seen in the Copilot audit log, from the first day of data to the week''s end.' })),
            @('Active users', 'M365 Copilot: people who used Copilot with a license in the week and still held one at its end. Copilot Chat: people who used it without a license in the week.'),
            @('Actions taken', 'Prompts submitted.'),
            @('Security alerts', 'Interactions with a jailbreak attempt, a prompt injection in content Copilot read, or a blocked resource.'),
            @('All time', "From $from (the first day of data) to the end of the week, so a report run again later gives the same figures."),
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

function Send-ReportMail {
    param([hashtable] $Setting, [string] $Subject, [string] $Html, [string] $Text,
          [hashtable] $ImageFiles, [string] $Workbook, [string[]] $ToList, [string[]] $CcList, [string[]] $BccList)
    if (-not $Setting.SmtpServer) { throw 'No SmtpServer in the settings.' }
    if (-not $Setting.From) { throw 'No From in the settings.' }
    $msg = New-Object System.Net.Mail.MailMessage
    $disposables = New-Object System.Collections.Generic.List[IDisposable]
    try {
        $msg.From = New-Object System.Net.Mail.MailAddress($Setting.From)
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

        $smtp = New-Object System.Net.Mail.SmtpClient($Setting.SmtpServer, [int] $Setting.SmtpPort)
        $disposables.Add($smtp)
        $smtp.EnableSsl = [bool] $Setting.SmtpUseSsl
        $smtp.UseDefaultCredentials = [bool] $Setting.SmtpUseDefaultCredentials
        $smtp.Timeout = 120000
        $smtp.Send($msg)
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
    $ds = Invoke-ReportQuery $conn 'EXEC bi.usp_WeeklyCopilotReport @WeekEnding = @w, @Weeks = @n, @ChatEnabled = @c;' @{
        '@w' = $week; '@n' = [int] $setting.Weeks; '@c' = [string] $setting.ChatEnabled }
    $meta = $ds.Tables[0].Rows[0]
    $table = $ds.Tables[1]
    $weeks = $ds.Tables[2]
    $complete = [bool] $meta.DataComplete

    $warnings = New-Object System.Collections.Generic.List[string]
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

    $workbook = Join-Path $outDir "Copilot weekly report $stamp.xlsx"
    try { New-ReportWorkbook -Meta $meta -Table $table -Weeks $weeks -Path $workbook }
    catch {
        Write-ReportLog "Workbook not written, the email goes without it: $($_.Exception.Message)" 'WARN'
        $workbook = $null
    }

    $subject = "$($setting.SubjectPrefix): week of $($meta.WeekLabel)"
    $text = New-ReportText -Meta $meta -Table $table

    if ($Preview) {
        $fileRefs = @{}
        foreach ($k in $images.Keys) { $fileRefs[$k] = [IO.Path]::GetFileName($images[$k]) }
        $htmlPath = Join-Path $outDir "Copilot weekly report $stamp.html"
        $html = New-ReportHtml -Meta $meta -Table $table -Images $fileRefs -Warnings $warnings
        [IO.File]::WriteAllText($htmlPath, $html, (New-Object Text.UTF8Encoding($false)))
        Write-ReportLog "Preview of the week ending ${stamp}: $htmlPath"
        if ($workbook) { Write-ReportLog "Workbook: $workbook" }
        Write-ReportLog "Subject would be: $subject"
        exit 0
    }

    $cids = @{}; $cidFiles = @{}
    foreach ($k in $images.Keys) { $cids[$k] = "cid:chart$k"; $cidFiles["chart$k"] = $images[$k] }
    $html = New-ReportHtml -Meta $meta -Table $table -Images $cids -Warnings $warnings
    if ($setting.KeepCopies) {
        [IO.File]::WriteAllText((Join-Path $outDir "Copilot weekly report $stamp.html"), $html, (New-Object Text.UTF8Encoding($false)))
    }

    if ($To) { $toList = $To; $ccList = @(); $bccList = @() }
    else { $toList = @($setting.To); $ccList = @($setting.Cc); $bccList = @($setting.Bcc) }
    $who = (@($toList) + @($ccList) + @($bccList) | Where-Object { $_ }) -join '; '
    $keepRecord = -not $To

    try {
        Send-ReportMail -Setting $setting -Subject $subject -Html $html -Text $text -ImageFiles $cidFiles -Workbook $workbook `
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
