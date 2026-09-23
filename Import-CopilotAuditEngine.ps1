<#
    Compiles CopilotAuditEngine.cs into the current PowerShell session.
    Dot-sourced by Invoke-CopilotAuditBackfill.ps1 and by the tests.
#>

function Import-CopilotAuditEngine {
    [CmdletBinding()]
    param([string[]] $ExtraSource)

    if (('CopilotAudit.BackfillEngine' -as [type]) -and -not $ExtraSource) { return }

    # Everything referenced ships with PowerShell 7 / .NET 6+ - nothing to install.
    $refs = @(
        'System.Data.SqlClient', 'System.Data.Common', 'System.Threading.Channels', 'System.Net.Http',
        'System.Net.Primitives', 'System.Text.Json', 'System.Collections', 'System.Collections.Concurrent',
        'System.Collections.NonGeneric', 'System.Linq', 'System.Runtime', 'System.Threading', 'System.Memory',
        'System.ComponentModel.Primitives', 'System.ComponentModel.TypeConverter', 'System.ComponentModel',
        'System.Text.Encoding.Extensions', 'System.Private.Uri', 'System.Console', 'System.IO.Compression', 'netstandard'
    )
    $sources = @(Join-Path $PSScriptRoot 'CopilotAuditEngine.cs') + @($ExtraSource | Where-Object { $_ })

    # -IgnoreWarnings: System.Data.SqlClient was built against an older
    # System.Data.Common, which the compiler reports (CS1701) and then unifies.
    Add-Type -Path $sources -ReferencedAssemblies $refs -IgnoreWarnings -WarningAction SilentlyContinue -ErrorAction Stop
}
