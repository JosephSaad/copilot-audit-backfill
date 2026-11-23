# The weekly Copilot report's settings (tools\Send-CopilotWeeklyReport.ps1).
# Copy this file to CopilotWeeklyReport.settings.psd1 in the same folder and fill it in.
@{
    # ---- The database --------------------------------------------------------
    # Leave both empty to use SqlServer and Database from copilot-audit.settings.psd1.
    SqlServer = ''
    Database  = ''
    # TrustServerCertificate = $true    # only if the nightly run needs it too

    # ---- The mail server ----------------------------------------------------
    # Nothing here: from 3.1.3 the report sends through the server, user name, password and From address set in
    # the dashboard's Admin > Email, read from the database. SmtpServer, SmtpPort, SmtpUseSsl,
    # SmtpUseDefaultCredentials and From from 3.1.2 are no longer read and can be deleted.

    # ---- The message --------------------------------------------------------
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
    KeepCopies  = $true        # keep each week's email, charts and workbook under reports\<week ending>
}
