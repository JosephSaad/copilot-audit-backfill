using System.Text.Json;
using Governance.Application.Data;
using Governance.Application.Requests;
using Governance.Application.Rules;
using Governance.Application.Workflow;
using Governance.Domain;

namespace Governance.Application.Commands;

public sealed record ClassificationInput(
    int? CostCenterId, string? SolutionClass, string? LifecycleStage, int? ServiceId, string? Criticality, string? TriggerType,
    string? DataBoundary, string? DataClass, string? Audience, string? EnvironmentClassCode, IReadOnlyList<string> Meters, bool MetersNone);

public sealed record CheckInput(int ControlCheckId, bool Passed, string? Actual, UploadedFile? Evidence);

/// <summary>Page commands for requests and their tasks. The workflow engine enforces stages and segregation of duties.</summary>
public sealed class RequestCommands(CommandRunner run, WorkflowEngine engine, EvidenceStore evidence, IClock clock)
{
    private ICurrentPrincipal Me => run.Principal;

    public Task<string?> CreateDraftAsync(Guid idem, string journey, string title, int fundingOwnerId, int? serviceId, int? sponsorPersonId, Guid? workloadPublicId, RequestPayload payload)
        => run.RunAsync(idem, "request.draft", async uow =>
        {
            if (journey == Journeys.Commercial)
                run.RequireAny(Permissions.RegistryFinance, Permissions.CommercialView);
            int? workloadId = null;
            if (workloadPublicId is Guid wp)
            {
                workloadId = await WorkloadIdAsync(uow, wp);
                payload.ExistingWorkloadPublicId = wp;
            }
            await RequireCanRequestForAsync(uow, journey, fundingOwnerId, workloadId);
            await RequireValidSponsorAsync(uow, sponsorPersonId, fundingOwnerId);
            var r = await engine.CreateDraftAsync(uow, Me, journey, title, fundingOwnerId, serviceId, sponsorPersonId, workloadId, payload);
            return r.PublicId.ToString();
        });

    public Task UpdateDraftAsync(Guid idem, Guid requestPublicId, string title, int fundingOwnerId, int? serviceId, int? sponsorPersonId, Guid? workloadPublicId, RequestPayload payload, byte[]? rowVer)
        => run.RunAsync(idem, "request.update", async uow =>
        {
            var id = await RequestIdAsync(uow, requestPublicId);
            int? workloadId = workloadPublicId is Guid wp ? await WorkloadIdAsync(uow, wp) : null;
            await RequireValidSponsorAsync(uow, sponsorPersonId, fundingOwnerId);
            await engine.UpdateDraftAsync(uow, Me, id, title, fundingOwnerId, serviceId, sponsorPersonId, workloadId, payload, rowVer);
        });

    public Task SubmitAsync(Guid idem, Guid requestPublicId)
        => run.RunAsync(idem, "request.submit", async uow =>
        {
            var id = await RequestIdAsync(uow, requestPublicId);
            var r = await engine.GetRequestAsync(uow, id);
            // B7 resubmission goes through the remediation task; a draft is submitted.
            if (r.CurrentStage == Stages.Remediate)
            {
                var task = await uow.QueryFirstOrDefaultAsync<TaskRow>("SELECT * FROM wf.Task WHERE RequestId = @id AND StageCode = 'B7' AND Status = 'OPEN'", new { id })
                           ?? throw new DomainException("There is no open remediation task.");
                await engine.CompleteWorkAsync(uow, Me, task.TaskId, StageOutcomes.Resubmit, "Resubmitted after remediation.");
                return;
            }
            await engine.SubmitAsync(uow, Me, id);
        });

    public Task WithdrawAsync(Guid idem, Guid requestPublicId)
        => run.RunAsync(idem, "request.withdraw", async uow => await engine.WithdrawAsync(uow, Me, await RequestIdAsync(uow, requestPublicId)));

    public Task ClaimAsync(Guid idem, Guid taskPublicId)
        => run.RunAsync(idem, "task.claim", async uow => await engine.ClaimAsync(uow, Me, await TaskIdAsync(uow, taskPublicId)));

    public Task ReleaseAsync(Guid idem, Guid taskPublicId)
        => run.RunAsync(idem, "task.release", async uow => await engine.ReleaseAsync(uow, Me, await TaskIdAsync(uow, taskPublicId)));

    public Task CompleteAsync(Guid idem, Guid taskPublicId, string outcome, string? notes, UploadedFile? file = null, string? evidenceType = null)
        => run.RunAsync(idem, "task.complete", async uow =>
        {
            var taskId = await TaskIdAsync(uow, taskPublicId);
            var t = await engine.GetTaskAsync(uow, taskId);
            if (t.TaskType == TaskTypes.Verification) throw new DomainException("Use the verification form to verify.");
            if (file is not null && t.RequestId is long rid)
            {
                var r = await engine.GetRequestAsync(uow, rid);
                await evidence.SaveAsync(uow, file, evidenceType ?? "OTHER", notes, r.FundingOwnerId, new[] { new EvidenceLinkTarget("REQUEST", rid) });
            }
            if (t.StageCode == Stages.BuildAndTest && t.RequestId is long rid2
                && await uow.ScalarAsync<int>("SELECT COUNT(*) FROM evd.EvidenceLink WHERE LinkType = 'REQUEST' AND LinkId = @rid2", new { rid2 }) == 0)
                throw new DomainException("Upload the test results (charging, attribution, exhaustion, overage, recovery and negative tests) before marking the build ready.");
            await engine.CompleteWorkAsync(uow, Me, taskId, outcome, CommandRunner.TrimN(notes, 2000));
        });

    public Task<string?> DecideAsync(Guid idem, Guid taskPublicId, DecisionInput input, UploadedFile? file = null)
        => run.RunAsync(idem, "decision.record", async uow =>
        {
            var taskId = await TaskIdAsync(uow, taskPublicId);
            var t = await engine.GetTaskAsync(uow, taskId);
            var decisionId = await engine.DecideAsync(uow, Me, taskId, input);
            if (file is not null)
                await evidence.SaveAsync(uow, file, "OTHER", "Attached to the decision.", t.FundingOwnerId, new[] { new EvidenceLinkTarget("DECISION", decisionId) });
            return decisionId.ToString();
        });

    public Task UploadAsync(Guid idem, Guid requestPublicId, UploadedFile file, string evidenceType, string? description)
        => run.RunAsync(idem, "evidence.upload", async uow =>
        {
            var id = await RequestIdAsync(uow, requestPublicId);
            var r = await engine.GetRequestAsync(uow, id);
            var involved = r.RequesterPersonId == Me.PersonId || r.SponsorPersonId == Me.PersonId || Me.IsCentral
                           || await uow.ScalarAsync<int>("SELECT COUNT(*) FROM wf.Task WHERE RequestId = @id AND (AssignedPersonId = @p OR CompletedByPersonId = @p)", new { id, p = Me.PersonId }) > 0
                           || Me.Can(Permissions.WorkloadView, r.FundingOwnerId);
            if (!involved) throw new DomainException("Only people involved in this request can add evidence.");
            await evidence.SaveAsync(uow, file, evidenceType, description, r.FundingOwnerId, new[] { new EvidenceLinkTarget("REQUEST", id) });
            await run.AuditAsync(uow, "evidence.upload", "Request", r.RequestNumber, file.FileName);
        });

    /// <summary>B3: the four independent classification fields (C03) and the rest of the record, saved on the workload.</summary>
    public Task SaveClassificationAsync(Guid idem, Guid requestPublicId, ClassificationInput input)
        => run.RunAsync(idem, "workload.classify", async uow =>
        {
            var id = await RequestIdAsync(uow, requestPublicId);
            var r = await engine.GetRequestAsync(uow, id);
            if (r.CurrentStage != Stages.Classify) throw new DomainException("Classification is saved while the request is at B3.");
            if (r.WorkloadId is not int wid) throw new DomainException("The request has no workload record yet.");
            var task = await uow.QueryFirstOrDefaultAsync<TaskRow>("SELECT * FROM wf.Task WHERE RequestId = @id AND StageCode = 'B3' AND Status = 'OPEN'", new { id });
            if (task is null) throw new DomainException("There is no open classification task.");
            var holds = Me.HasRole(Roles.Architect) || task.AssignedPersonId == Me.PersonId;
            if (!holds) throw new DomainException("Classification is done by Architecture (B3).");
            if (input.CostCenterId is int cc && await uow.ScalarAsync<int>("SELECT COUNT(*) FROM core.CostCenter WHERE CostCenterId = @cc AND FundingOwnerId = @FundingOwnerId AND EffectiveTo IS NULL", new { cc, r.FundingOwnerId }) == 0)
                throw new DomainException("The cost center must belong to the request's funding owner.");
            Check(input.SolutionClass, SolutionClasses.All, "solution class");
            Check(input.LifecycleStage, LifecycleStages.All, "lifecycle stage");
            Check(input.Criticality, Criticalities.All, "criticality");
            Check(input.TriggerType, TriggerTypes.All, "trigger type");
            Check(input.DataClass, DataClasses.Ordered, "data class");
            await uow.ExecuteAsync(
                """
                UPDATE core.Workload SET CostCenterId = @CostCenterId, SolutionClass = @SolutionClass, LifecycleStage = @LifecycleStage, ServiceId = COALESCE(@ServiceId, ServiceId),
                       Criticality = @Criticality, TriggerType = @TriggerType, DataBoundary = @DataBoundary, DataClass = @DataClass, Audience = @Audience,
                       EnvironmentClassCode = @EnvironmentClassCode
                WHERE WorkloadId = @wid
                """,
                new
                {
                    input.CostCenterId, input.SolutionClass, input.LifecycleStage, input.ServiceId, input.Criticality, input.TriggerType,
                    DataBoundary = CommandRunner.TrimN(input.DataBoundary, 400), input.DataClass, Audience = CommandRunner.TrimN(input.Audience, 400),
                    EnvironmentClassCode = string.IsNullOrEmpty(input.EnvironmentClassCode) ? null : input.EnvironmentClassCode, wid,
                });
            await uow.ExecuteAsync("DELETE FROM core.WorkloadMeter WHERE WorkloadId = @wid AND MeterCode NOT IN @keep", new { wid, keep = input.Meters.DefaultIfEmpty("~").ToArray() });
            foreach (var m in input.Meters.Distinct())
                await uow.ExecuteAsync("IF NOT EXISTS (SELECT 1 FROM core.WorkloadMeter WHERE WorkloadId = @wid AND MeterCode = @m) INSERT core.WorkloadMeter (WorkloadId, MeterCode) VALUES (@wid, @m)", new { wid, m = CommandRunner.Trim(m, 40) });
            await uow.ExecuteAsync("UPDATE wf.Request SET MetersDeclaredNone = @none WHERE RequestId = @id", new { none = input.MetersNone && input.Meters.Count == 0, id });
            await engine.EventAsync(uow, id, r.FundingOwnerId, Me.PersonId, "CLASSIFIED", r.State, r.State, Stages.Classify, "Classification saved.");
            await run.AuditAsync(uow, "workload.classify", "Workload", wid, JsonSerializer.Serialize(input with { Meters = input.Meters }));
        });

    /// <summary>
    /// D1: records the verification (ledger) with every check's expected and actual value, then completes the task
    /// with PASS or FAIL. Attestation checks need evidence; the explicit deny case must be tested; SoD3 in the engine.
    /// </summary>
    public Task<string?> VerifyAsync(Guid idem, Guid taskPublicId, IReadOnlyList<CheckInput> checks, string? configurationVersion, string? notes, bool recordFailure = false)
        => run.RunAsync(idem, "request.verify", async uow =>
        {
            var taskId = await TaskIdAsync(uow, taskPublicId);
            var t = await engine.GetTaskAsync(uow, taskId);
            if (t.TaskType != TaskTypes.Verification || t.Status != TaskStatuses.Open) throw new DomainException("This is not an open verification task.");
            var r = await engine.GetRequestAsync(uow, t.RequestId!.Value);
            var serviceId = r.ServiceId ?? await uow.ScalarAsync<int?>("SELECT ServiceId FROM core.Workload WHERE WorkloadId = @WorkloadId", new { r.WorkloadId });
            var template = await uow.QueryFirstOrDefaultAsync<(int TemplateId, string SettingsJson)>("SELECT TemplateId, SettingsJson FROM core.ControlTemplate WHERE ServiceId = @serviceId AND Status = 'ACTIVE'", new { serviceId });
            var defs = template.TemplateId == 0 ? new List<ControlCheckDef>()
                : (await uow.QueryAsync<ControlCheckDef>("SELECT ControlCheckId, CheckCode, Description, Method, SettingKey, IsDenyCase FROM core.ControlCheck WHERE TemplateId = @TemplateId ORDER BY SortOrder", new { template.TemplateId })).ToList();
            if (defs.Count == 0) throw new DomainException("The service has no active control template with checks, so nothing can be verified. Ask the Platform Owner to activate one.");
            if (r.ConfigurerPersonId == Me.PersonId) throw new DomainException("You configured this change, so a second operator verifies it (SoD3).");
            if (recordFailure && string.IsNullOrWhiteSpace(notes)) throw new DomainException("Say what failed in the notes, so the operator who configures it again knows what to fix.");
            var settings = Settings(template.SettingsJson);
            var results = new List<VerificationComplete.CheckResult>();
            var evidenceFiles = new List<UploadedFile>();
            foreach (var d in defs)
            {
                var input = checks.FirstOrDefault(c => c.ControlCheckId == d.ControlCheckId);
                var expected = d.SettingKey is not null && settings.TryGetValue(d.SettingKey, out var e) ? e : null;
                if (input?.Evidence is not null) evidenceFiles.Add(input.Evidence);
                results.Add(new VerificationComplete.CheckResult(d.CheckCode, d.Method, d.IsDenyCase, expected, CommandRunner.TrimN(input?.Actual, 200),
                    input?.Passed ?? false, input?.Evidence is not null));
            }
            var readBackOnly = defs.Count > 0 && defs.All(d => d.Method == "READBACK");
            var rule = VerificationComplete.Evaluate(results, r.ConfigurerPersonId, Me.PersonId, readBackOnly);
            // "Passed" is checked in full before anything is written: a refusal leaves no ledger row and the form keeps
            // what was entered. Only an explicit "failed" writes a FAIL row and sends the change back to fulfillment.
            if (!recordFailure && !rule.Passed)
            {
                var named = rule.Reasons.Select(reason =>
                {
                    var d = defs.FirstOrDefault(x => reason.StartsWith(x.CheckCode + ":", StringComparison.Ordinal));
                    return d is null ? reason : $"“{d.Description}”{reason[d.CheckCode.Length..]}";
                });
                throw new DomainException("Not recorded yet. Fix these, then record again, or record it as failed: " + string.Join(" ", named));
            }
            var pass = !recordFailure && rule.Passed;
            var publicId = Guid.NewGuid();
            var verificationId = await uow.ScalarAsync<long>(
                """
                INSERT evd.Verification (PublicId, RequestId, FundingOwnerId, TemplateId, ConfiguredByPersonId, VerifierPersonId, Method, Result, ChecksJson, ConfigurationVersion, VerifiedUtc)
                OUTPUT inserted.VerificationId
                VALUES (@publicId, @RequestId, @FundingOwnerId, @tpl, @ConfigurerPersonId, @me, @method, @result, @json, @cfg, @now)
                """,
                new
                {
                    publicId, r.RequestId, r.FundingOwnerId, tpl = template.TemplateId == 0 ? (int?)null : template.TemplateId, r.ConfigurerPersonId, me = Me.PersonId,
                    method = readBackOnly ? "READBACK" : defs.Any(d => d.Method == "READBACK") ? "MIXED" : "ATTESTATION", result = pass ? "PASS" : "FAIL",
                    json = JsonSerializer.Serialize(results), cfg = CommandRunner.TrimN(configurationVersion, 100), now = clock.UtcNow,
                });
            foreach (var f in evidenceFiles)
                await evidence.SaveAsync(uow, f, "TEST_RESULT", "Verification evidence.", r.FundingOwnerId, new[] { new EvidenceLinkTarget("VERIFICATION", verificationId) });
            var why = pass ? notes : notes + (rule.Passed ? "" : " " + string.Join(" ", rule.Reasons));
            await engine.CompleteWorkAsync(uow, Me, taskId, pass ? StageOutcomes.Pass : StageOutcomes.Fail, CommandRunner.TrimN(why, 2000));
            if (!pass)
                await uow.ExecuteAsync(
                    """
                    IF NOT EXISTS (SELECT 1 FROM ntf.Outbox WHERE DedupeKey = @key)
                    INSERT ntf.Outbox (EventType, Payload, DedupeKey) VALUES ('N08', @payload, @key)
                    """,
                    new
                    {
                        key = $"N08:{verificationId}",
                        payload = new Outbox.OutboxMessage
                        {
                            EventType = "N08", RequestId = r.RequestId, FundingOwnerId = r.FundingOwnerId,
                            PersonIds = r.ConfigurerPersonId is int cp ? new List<int> { cp } : new List<int>(),
                            Roles = { new Outbox.RoleTarget(Roles.PlatformOperator, ScopeTypes.Tenant, null) },
                            Subject = $"{r.RequestNumber}: verification failed", Text = CommandRunner.Trim(why, 1000), LinkPath = $"/Requests/{r.PublicId}", ActivityType = "verificationFailed",
                        }.ToJson(),
                    });
            return pass ? "PASS" : "FAIL";
        });

    private sealed class ControlCheckDef
    {
        public int ControlCheckId { get; init; }
        public string CheckCode { get; init; } = "";
        public string Description { get; init; } = "";
        public string Method { get; init; } = "";
        public string? SettingKey { get; init; }
        public bool IsDenyCase { get; init; }
    }

    public static Dictionary<string, string> Settings(string? json)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return d;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return d;
            foreach (var p in doc.RootElement.EnumerateObject())
                d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();
        }
        catch (JsonException) { }
        return d;
    }

    private static void Check(string? value, IReadOnlyList<string> allowed, string what)
    {
        if (value is not null && !allowed.Contains(value)) throw new DomainException($"Choose a valid {what}.");
    }

    private async Task RequireCanRequestForAsync(Uow uow, string journey, int fundingOwnerId, int? workloadId)
    {
        var fo = await uow.QuerySingleOrDefaultAsync<(string Status, string Name)>("SELECT Status, Name FROM core.FundingOwner WHERE FundingOwnerId = @fundingOwnerId", new { fundingOwnerId });
        if (fo.Name is null) throw new DomainException("Choose a funding owner.");
        if (journey is Journeys.CapacityChange or Journeys.Retirement)
        {
            if (workloadId is not int wid) throw new DomainException("Choose the workload.");
            var w = await uow.QuerySingleAsync<Workload>("SELECT * FROM core.Workload WHERE WorkloadId = @wid", new { wid });
            if (w.FundingOwnerId != fundingOwnerId) throw new DomainException("The request's funding owner must be the workload's funding owner.");
            var mine = w.ProductOwnerPersonId == Me.PersonId || w.SponsorPersonId == Me.PersonId || Me.Can(Permissions.WorkloadEdit, w.FundingOwnerId)
                       || Me.HasRoleIn(Roles.LobBusinessOwner, w.FundingOwnerId) || Me.HasRoleIn(Roles.SharedServiceOwner, w.FundingOwnerId) || Me.IsCentral;
            if (!mine) throw new DomainException("Only the workload's product owner, sponsor or business owner can ask for this change.");
        }
    }

    /// <summary>A sponsor is someone else who sponsors, owns products or owns the business in the request's funding owner.</summary>
    private async Task RequireValidSponsorAsync(Uow uow, int? sponsorPersonId, int fundingOwnerId)
    {
        if (sponsorPersonId is not int sp) return;
        if (sp == Me.PersonId) throw new DomainException("Choose someone else as sponsor: the sponsor vouches for the request, so it cannot be the requester.");
        var ok = await uow.ScalarAsync<int>(
            """
            SELECT COUNT(*) FROM sec.RoleAssignment
            WHERE PersonId = @sp AND RoleCode IN @roles AND ScopeType = 'FUNDING_OWNER' AND ScopeId = @fundingOwnerId
              AND EffectiveFrom <= @today AND (EffectiveTo IS NULL OR EffectiveTo >= @today)
            """, new { sp, roles = Queries.RegistryQueries.SponsorRoles, fundingOwnerId, today = clock.UtcNow.Date });
        if (ok == 0) throw new DomainException("The sponsor must be a workload sponsor, product owner or business owner in the funding owner you chose.");
    }

    private static async Task<long> RequestIdAsync(Uow uow, Guid publicId)
        => await uow.ScalarAsync<long?>("SELECT RequestId FROM wf.Request WHERE PublicId = @publicId", new { publicId }) ?? throw new DomainException("Request not found.");

    private static async Task<long> TaskIdAsync(Uow uow, Guid publicId)
        => await uow.ScalarAsync<long?>("SELECT TaskId FROM wf.Task WHERE PublicId = @publicId", new { publicId }) ?? throw new DomainException("Task not found.");

    private static async Task<int> WorkloadIdAsync(Uow uow, Guid publicId)
        => await uow.ScalarAsync<int?>("SELECT WorkloadId FROM core.Workload WHERE PublicId = @publicId", new { publicId }) ?? throw new DomainException("Workload not found.");
}
