# The weekly Copilot report's settings (tools\Send-CopilotWeeklyReport.ps1).
# Copy this file to CopilotWeeklyReport.settings.psd1 in the same folder and fill it in.
@{
    # ---- The database --------------------------------------------------------
    # Leave both empty to use SqlServer and Database from copilot-audit.settings.psd1.
    SqlServer = ''
    Database  = ''
    # TrustServerCertificate = $true    # only if the nightly run needs it too

    # ---- The mail relay -----------------------------------------------------
    SmtpServer = 'smtp-relay.contoso.com'
    SmtpPort   = 25
    SmtpUseSsl = $false                  # $true: STARTTLS
    SmtpUseDefaultCredentials = $false   # $true: sign in to the relay as the task's account
    # A relay that takes a user name and password (Proofpoint, say): SmtpUseSsl = $true, SmtpPort = 587, and
    # SmtpUser set; then save the password once: .\tools\Send-CopilotWeeklyReport.ps1 -SaveSmtpPassword
    SmtpUser = ''                        # the relay's user name; never the password
    # SmtpPasswordPath = 'D:\Secrets\CopilotWeeklyReport.smtp-password'   # only if not beside this file
    # SmtpAllowPasswordWithoutSsl = $true   # only for a relay on a trusted network without STARTTLS

    # ---- The message --------------------------------------------------------
    From = 'Copilot Reports <copilot-reports@contoso.com>'
    To   = @('copilot-leadership@contoso.com')
    Cc   = @()
    Bcc  = @()
    SubjectPrefix = 'Copilot weekly report'     # the subject is "<prefix>: week of 10/3 - 10/9/2026"

    # ---- When ---------------------------------------------------------------
    # In the reporting time zone (audit.ReportingTimeZone(), the dashboard's), not the server's.
    SendDay  = 'Tuesday'
    SendTime = '08:00'
    MaxWaitHours = 24      # if the data has not reached the Monday after the week, wait up to this long, then send with a note
    MaxAttempts  = 8       # a failed send is tried again each hour, up to this many times

    # ---- What ---------------------------------------------------------------
    Weeks       = 13           # weeks in the charts, the reported week included (13 = about 90 days)
    ChatEnabled = 'AllUsers'   # Copilot Chat users enabled: AllUsers = everyone seen in the audit log since the data starts;
                               # Unlicensed = only those seen using Copilot without a license
    KeepCopies  = $true        # keep each week's email, charts and workbook under reports\<week ending>
}
