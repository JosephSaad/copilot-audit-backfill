# PSScriptAnalyzer settings for this repository. CI (ci.yml, the analyze job) runs every .ps1 with
# these settings and fails on any error or warning. A rule is excluded only for a reason written here;
# anything else the analyzer finds is fixed in the code.
@{
    Severity     = @('Error', 'Warning')
    ExcludeRules = @(
        # The scripts are run by an operator at a console or by the scheduled task, whose output is
        # captured to the log. Write-Host is the console half of Write-Log (which also writes the file).
        'PSAvoidUsingWriteHost',

        # Every script declares its parameters at the top and reads them inside Invoke-Main and the
        # helpers from script scope, so the rule cannot see them used. It reports only these.
        'PSReviewUnusedParameter',

        # The same pattern in the tests: variables set at script scope for a dot-sourced script, or
        # for a helper, to read. The rule reports only these.
        'PSUseDeclaredVarsMoreThanAssignments',

        # Private helpers such as New-*ConnString build a string; Set-Prop sets a property on an
        # object in memory. Nothing changes on the system. Every script that does change the
        # system declares SupportsShouldProcess itself (Install-*, Add-CopilotTenant, the job server).
        'PSUseShouldProcessForStateChangingFunctions',

        # Write-Log exists as a cmdlet only in PowerShell 6.1 on Windows, which nobody runs; the
        # scripts target Windows PowerShell 5.1 and PowerShell 7, where it is not defined.
        'PSAvoidOverwritingBuiltInCmdlets'
    )
}
