# Governance Workbench: runbooks

What to do when something in the Governance Workbench fails. The Workbench is not in the path of any Copilot
service: if it is down, approvals and verification wait, and Microsoft keeps enforcing limits. The rest of
Copilot Insights does not depend on it either. [OPERATIONS.md](OPERATIONS.md#governance-workbench) describes
the pages, the roles and the worker's jobs.

**Run a job again.** On the worker's server, as the worker's account, in the worker's folder:
`.\Governance.Worker.exe --once <job name>` (several: `--once DirectorySync,EntitlementSync`), then check
Workbench settings › Integrations. `--drain` delivers every notice due now. The job names are in
[OPERATIONS.md](OPERATIONS.md#worker-jobs). There is no button for it on the site.

## Every /workbench address says Page not found

The site's settings do not name the Workbench's database. Set `ConnectionStrings:Governance` in
`appsettings.Production.json` on every web server and recycle the app pool. Without it the Workbench is off on
purpose and the rest of the dashboard works as before.

## A page says "This page is not available to you"

Page access comes from the person's grants in Admin › Access: Governance, GovernanceOperator,
GovernanceAuditor or Admin ([OPERATIONS.md](OPERATIONS.md#who-opens-which-pages)). Admin › Access ›
*Check someone's access* shows the Workbench pages their grants open and their Workbench roles. If they can
open a page but not act on it (no Approve, no seats of a line of business), the missing piece is a Workbench
role: Workbench settings › Workbench roles, or the line of business's funding owner page.

## Integration permission failure

**Signs:** N19 alert; Workbench settings › Integrations shows a failed job or integration.

1. Find the job and the call in the worker's log at the time of the failed run (errors are logged, never
   shown on a page).
2. 401: the certificate is missing, expired or not readable by the service account (see certificate rotation).
3. 403 on Graph: check the worker app's granted application permissions in Entra (`User.Read.All`,
   `GroupMember.Read.All`, `AccessReview.Read.All`; for Copilot seats also `LicenseAssignment.Read.All`, and
   `Reports.Read.All` when the seat read uses Microsoft's usage report) and that admin consent is granted. A
   refused usage report does not fail the seat read: the run says so and no seat is proposed for reclaim.
4. 403 on access packages: the worker must be catalog reader on the governed catalog. Re-add it in Identity
   Governance › Catalogs › Roles and administrators.
5. 403 on Power Platform: the worker's Power Platform reader role (preview). Until it is fixed, the licensing
   read is skipped, and an operator reads it from Governance operations › Sync and freshness with their own
   Entra sign-in.
6. Run the job again (above) and confirm it succeeds.

## Certificate rotation

The worker finds its certificate by subject name (`Tenant:WorkerCertificateSubject`, `CN=CopilotGovernanceWorker`
in production), the newest valid one first, so a new certificate needs no configuration change. The site signs
people in as Copilot Insights does; its Entra certificate, where it has one, is covered in
[ENTRA-SIGN-IN.md](ENTRA-SIGN-IN.md).

1. Create the new certificate with the same subject in `LocalMachine\My` and give the worker's account read
   access to its private key:
   `pwsh -File governance\deploy\Grant-CertificateKeyAccess.ps1 -Subject 'CN=CopilotGovernanceWorker' -Account 'CORP\gmsa-cgw-worker$'`.
2. Upload its public key to the worker's app registration (Certificates & secrets). Keep the old one for now.
3. Restart the `CopilotGovernanceWorker` service.
4. Confirm a successful job run, then remove the old certificate from the app and the store.
5. `governance\deploy\Test-WorkbenchPrerequisites.ps1` warns when a certificate has 30 days or fewer left.

## Stale sync

**Signs:** a staleness note on pages that show Microsoft data; N21 for inventory older than 7 days.

1. Directory and entitlement sync: check the job's last run on Workbench settings › Integrations and the
   worker's log; fix permissions; run it again. A lost delta token makes the next run a full read; that is
   expected and slower.
2. Copilot seats: the worker reads them daily, app-only. Activity comes from Copilot Insights' own records
   when the worker has `ConnectionStrings:Insights`; check that connection and that the nightly pipeline ran
   (Admin › Health). Without it, the usage report is read: if the run says the report conceals names, turn off
   concealed names in the Microsoft 365 admin center (Settings › Org settings › Reports). If a seat group is
   not found, check its name on Workbench settings › Seat groups and that Entra Connect syncs it. An operator
   can read seats at once from Governance operations › Sync and freshness › *Read seats now*.
3. Inventory: the inventory API works only with an operator's own sign-in. An operator opens Sync and
   freshness and runs it, also before every verification.

## Dead letters

**Signs:** Workbench settings › Integrations shows dead letters above zero.

1. Read the worker's log for those messages to find the cause (mailbox scope, Teams app not installed for the
   person, flow owner left, ITSM down).
2. Fix the cause. New notices then go out as usual.
3. This build has no replay for a dead-lettered message. Email is the notice of record and every task is on
   the person's My work, so nobody loses a task; tell the people concerned if a notice mattered. For an ITSM
   ticket that was never created (the request shows it as *being created*), raise the task by hand in the
   ITSM and tell the intake team.
   <!-- 3.0.0: no dead-letter replay on Workbench settings › Integrations yet -->



## SQL failover

1. The availability group listener moves; the site and the worker reconnect on the next call.
2. Check Workbench settings › Integrations: every job should run within its schedule. A job holds its lock in
   a SQL session, so a job interrupted by the failover simply runs again.
3. Outbox rows claimed before the failover are claimed again after their 60-second lock expires.
4. Verify the ledger against the saved digests after any restore
   (`governance\deploy\Save-LedgerDigest.ps1 ... -Verify`, [OPERATIONS.md](OPERATIONS.md#the-ledger-digest)).

## Microsoft API change

The named change-watch owner reads the Microsoft 365 message center and the Power Platform release notes.

1. Open a material change assessment (I3) for anything that touches a governed service.
2. Rerun the contract tests (`governance\tests`) against the test tenant; a failing contract test shows what
   changed.
3. Update the adapter behind its interface; preview and beta calls stay behind feature flags and never
   drive a control.
4. Rerun the affected control templates' verification checks.

## Teams app policy change

**Signs:** activity notices fail for some people; the delivery log shows "app not installed".

1. Check the Teams app setup policy still pre-installs the Workbench app for Workbench users.
2. Check the app is still allowed in the org catalog and that resource-specific consent is allowed.
3. Email remains the notice of record, so nobody misses a task while this is fixed.

## Exchange RBAC re-scope (notification mailbox)

The worker sends mail only from one mailbox, through Exchange RBAC for Applications. Never also grant
`Mail.Send` in Entra: grants add up, and an Entra grant removes the scope.

1. Check: `Test-ServicePrincipalAuthorization -Identity <worker service principal object ID> -Resource <mailbox>`.
2. If the mailbox changed, update the management scope's recipient filter to the new address and the
   worker's `Tenant:Mailbox` setting.
3. Changes take 30 minutes to 2 hours to apply. Email retries meanwhile; the SMTP relay
   (`Notifications:SmtpHost`) is the fallback.
