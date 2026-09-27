<#
.SYNOPSIS
    Catches a column added before its table is created: in every sql/ script, an ALTER TABLE ... ADD
    on a table the same script creates must come after the CREATE TABLE.

.DESCRIPTION
    The scripts are idempotent and run again on every install, so an ALTER guarded by the table's
    existence and placed above its CREATE TABLE is skipped on a fresh install and only takes effect
    the second time. That hid dash.Extract's retry columns in 2.4.0's first build. A second run
    masks it, so the integration tests cannot be relied on to see it; this reads the scripts.

.EXAMPLE
    pwsh -NoProfile -File tests/Test-SqlScriptOrder.ps1
#>
#Requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$failures = 0
$checked = 0
foreach ($f in Get-ChildItem -LiteralPath (Join-Path $root 'sql') -Filter *.sql) {
    $text = [System.IO.File]::ReadAllText($f.FullName)
    $created = @{}
    foreach ($m in [regex]::Matches($text, '(?i)\bCREATE\s+TABLE\s+(\w+\.\w+)')) {
        if (-not $created.ContainsKey($m.Groups[1].Value)) { $created[$m.Groups[1].Value] = $m.Index }
    }
    foreach ($m in [regex]::Matches($text, '(?i)\bALTER\s+TABLE\s+(\w+\.\w+)\s+ADD\b')) {
        $t = $m.Groups[1].Value
        if (-not $created.ContainsKey($t)) { continue }   # created by another script, which runs first
        $checked++
        if ($m.Index -lt $created[$t]) {
            $line = ($text.Substring(0, $m.Index) -split "`n").Count
            Write-Host "  FAIL  $($f.Name):$line adds to $t above its CREATE TABLE" -ForegroundColor Red
            $failures++
        }
    }
}
Write-Host "Checked $checked ALTER TABLE ... ADD statement(s) against their CREATE TABLE."
if ($failures) { Write-Host "$failures column addition(s) come before their table is created: move each below its CREATE TABLE." -ForegroundColor Red; exit 1 }
Write-Host 'Every column addition follows its CREATE TABLE.' -ForegroundColor Green
