# Runbooks

What to do when something fails. The Workbench is not in the path of any Copilot service: if it is down,
approvals and verification wait, and Microsoft keeps enforcing limits.

## Integration permission failure

**Signs:** N19 alert; Admin › Integrations shows a red integration with a reference such as `X2 403`.

1. Find the integration and the call in the worker log by the reference (errors are logged, never shown).
2. 401: the certificate is missing, expired or not readable by the service account (see certificate rotation).
3. 403 on Graph: check the Worker app's granted application permissions in Entra (`User.Read.All`,
   `GroupMember.Read.All`, `AccessReview.Read.All`) and that admin consent is granted.
4. 403 on access packages: the Worker must be catalog reader on the governed catalog. Re-add it in Identity
   Governance › Catalogs › Roles and administrators.
5. 403 on Power Platform: the Worker's Power Platform reader role (preview). Until fixed, operators run the
   licensing read from Operations › Sync with their own sign-in.
6. Run the job again from Admin › Integrations and confirm it turns green.

## Certificate rotation

Certificates are selected by subject name, so a new certificate needs no configuration change.

1. Create the new certificate with the same subject (`CN=CopilotGovernanceWorker` or `...Web`) in
   `LocalMachine\My` and give the service account read access to its private key.
2. Upload its public key to the app registration (Certificates & secrets). Keep the old one for now.
3. Restart the worker or recycle the web app pool. Both pick the newest valid certificate with the subject.
4. Confirm a successful job run, then remove the old certificate from the app and the store.
5. Alerts start 30 days before expiry.

## Stale sync

**Signs:** a staleness banner on pages that show Microsoft data; N21 for inventory older than 7 days.

1. Directory and entitlement sync: check the job's last run and error reference; fix permissions; run again.
   A lost delta token makes the next run a full read; that is expected and slower.
2. Inventory: the inventory API works only with an operator's sign-in. An operator opens Operations › Sync
   and runs it. Do this before every verification (D1) too.

## Dead-letter replay

**Signs:** N19 for dead-letter growth; Admin › Integrations shows dead-lettered messages.

1. Read the last error of a few messages to find the cause (mailbox scope, Teams app not installed for the
   person, flow owner left, ITSM down).
2. Fix the cause first.
3. Replay from Admin › Integrations. Replays keep the original de-duplication key, so nothing is delivered
   twice.

## SQL failover

1. The availability group listener moves; the web and worker reconnect on the next call.
2. Check Admin › Integrations: every job should run within its schedule. Jobs hold their singleton lock in a
   session, so a job interrupted by the failover simply runs again.
3. Outbox rows claimed before the failover are reclaimed after their 60-second lock expires.
4. Verify the ledger digest against the copy on write-once storage after any restore.

## Microsoft API change

The named change-watch owner reads the Microsoft 365 message center and the Power Platform release notes.

1. Open a material change assessment (I3) for anything that touches a governed service.
2. Rerun the contract tests against the test tenant; a failing contract test shows what changed.
3. Update the adapter behind its interface; preview and beta calls stay behind feature flags and never
   drive a control.
4. Rerun the affected control templates' verification checks.

## Teams app policy change

**Signs:** activity notices fail for some people; the delivery log shows "app not installed".

1. Check the Teams app setup policy still pre-installs the Workbench app for Workbench users.
2. Check the app is still allowed in the org catalog and that resource-specific consent is allowed.
3. Email remains the notice of record, so nobody misses a task while this is fixed.

## Exchange RBAC re-scope (notification mailbox)

The Worker sends mail only from one mailbox, through Exchange RBAC for Applications. Never also grant
`Mail.Send` in Entra: grants add up, and an Entra grant removes the scope.

1. Check: `Test-ServicePrincipalAuthorization -Identity <Worker service principal object ID> -Resource <mailbox>`.
2. If the mailbox changed, update the management scope's recipient filter to the new address and the
   Workbench setting for the sender.
3. Changes take 30 minutes to 2 hours to apply. Email retries meanwhile; the SMTP relay is the fallback.
