using Governance.Application.Workflow;
using Governance.Domain;

namespace Governance.Web.Infrastructure;

/// <summary>
/// Plain words for every code a page can show. Codes stay in the database and URLs; people read these labels.
/// Unknown codes fall back to sentence case ("SOME_CODE" becomes "Some code").
/// </summary>
public static class Labels
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        // Stage outcomes (transition keys)
        ["COMPLETE"] = "Complete", ["RETURN"] = "Returned to the requester", ["RESUBMIT"] = "Resubmitted", ["WITHDRAW"] = "Withdrawn",
        ["AUTO_CLOSE"] = "Closed after no response", ["CLASSIFIED"] = "Classified", ["FITS"] = "Fits an approved pattern",
        ["NO_PATTERN"] = "No approved pattern fits", ["REVIEWED"] = "Reviews finished", ["READY"] = "Ready for fulfillment",
        ["TICKET_CLOSED"] = "ITSM ticket closed", ["TICKET_CANCELLED"] = "ITSM ticket cancelled", ["PASS"] = "Passed",
        ["PASS_GATE"] = "Passed; production acceptance next", ["FAIL"] = "Failed", ["ACCEPTED"] = "Accepted", ["REJECTED"] = "Not accepted",
        ["EXECUTED"] = "Executed", ["HOLD"] = "On hold",

        // Decision sources
        ["WORKBENCH"] = "Recorded in the Workbench", ["ENTRA"] = "Imported from Entra", ["MIGRATED"] = "Migrated record",

        // Timeline event types
        ["CREATED"] = "Created", ["EDITED"] = "Edited", ["SUBMITTED"] = "Submitted", ["TRANSITION"] = "Moved", ["DECISION"] = "Decision recorded",
        ["PATTERN_CHECK"] = "Pattern check", ["REVIEWS_OPENED"] = "Reviews opened", ["EVIDENCE_SUPPLIED"] = "Evidence supplied", ["WITHDRAWN"] = "Withdrawn",
        ["ITSM_CLOSED"] = "ITSM ticket closed", ["ITSM_CANCELLED"] = "ITSM ticket cancelled", ["ITSM_RESOLVED"] = "ITSM ticket resolved",
        ["ESCALATED"] = "Escalated", ["REMINDER"] = "Reminder sent",

        // Task types
        ["WORK"] = "Work", ["CONSULT"] = "Consultation", ["VERIFICATION"] = "Verification", ["ATTESTATION"] = "Attestation",
        ["RECERTIFICATION"] = "Recertification", ["FINDING"] = "Finding", ["EVENT"] = "Event", ["CLAIM"] = "Claim", ["ACCEPT_ROLE"] = "Accept a role",
        ["ACTIVATION"] = "Activation",

        // Service catalog
        ["PPAC_ENVIRONMENT"] = "Power Platform environment", ["M365_POLICY"] = "Microsoft 365 policy", ["ENTRA_PACKAGE"] = "Entra access package",
        ["GA"] = "Generally available", ["PREVIEW"] = "Preview", ["NOT_AVAILABLE"] = "Not available in the tenant",
        ["PILOT"] = "Pilot", ["NOT_APPROVED"] = "Not approved",

        // Verification methods
        ["READBACK"] = "Read back from Microsoft", ["TEST_EVIDENCE"] = "Test evidence", ["MIXED"] = "Read-back and attestation",

        // SLA policies
        ["NONE"] = "No SLA", ["SAME_DAY"] = "Same business day", ["BD1"] = "1 business day", ["BD2"] = "2 business days", ["BD3"] = "3 business days",
        ["BD5"] = "5 business days", ["BD10"] = "10 business days", ["BD15"] = "15 business days",
        ["EVT_CRITICAL"] = "Critical event: 1 hour, around the clock", ["EVT_HIGH"] = "High event: 4 hours", ["EVT_NORMAL"] = "Normal event: 1 business day",

        // Funding owners, units, misc
        ["LOB"] = "Line of business", ["ENTERPRISE_SHARED"] = "Enterprise Shared", ["COPILOT_CREDIT"] = "Copilot Credits", ["P3_COMMIT_UNIT"] = "P3 commit units",
        ["PAYG_SCOPE"] = "PAYG scope", ["P3"] = "P3 plan", ["PACK"] = "Monthly pack", ["LICENSE"] = "License",
        ["PRODUCTIVITY_SOLUTION"] = "Productivity solution", ["BUSINESS_APPLICATION"] = "Business application",
        ["USER_INITIATED"] = "User-initiated", ["CUSTOMER_TRIGGERED"] = "Customer-triggered",
        ["APP_ONLY"] = "Worker (app-only)", ["DELEGATED"] = "Signed-in operator", ["UPLOAD"] = "Uploaded file", ["TEST_DOUBLE"] = "Test double",
        ["UPLOAD_UNSCANNED"] = "Uploaded (not scanned)", ["GRAPH_BETA"] = "Graph beta (evidence only)", ["POWER_PLATFORM"] = "Power Platform", ["GRAPH"] = "Microsoft Graph",
        ["OK"] = "OK", ["DENIED"] = "Denied", ["FAILED"] = "Failed", ["SUCCEEDED"] = "Succeeded", ["RUNNING"] = "Running", ["PARTIAL"] = "Partly done",
        ["OVERDUE"] = "Overdue", ["SOD_VIOLATION"] = "Segregation of duties breach",
        ["REQUESTER"] = "Requester",
    };

    /// <summary>Plain words for a code. Request states, decision types, outcomes and SoD rules use the domain's own labels.</summary>
    public static string For(string? code)
    {
        if (string.IsNullOrEmpty(code)) return "";
        if (Map.TryGetValue(code, out var s)) return s;
        if (System.Text.RegularExpressions.Regex.IsMatch(code, "^[a-z]+(\\.[a-z_]+)+$")) return Action(code);
        // Only code-shaped strings (UPPER_CASE) are translated; anything else is already words.
        if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z0-9_/-]+$")) return code;
        if (code.StartsWith("SOD", StringComparison.Ordinal) && code.Length == 4) return AssignmentResolver.SodText(code);
        var state = RequestStates.Label(code);
        if (state != code) return state;
        var type = DecisionTypes.Label(code);
        if (type != code) return type;
        var outcome = Outcomes.Label(code);
        if (outcome != code) return outcome;
        return code.Length <= 3 ? code : Sentence(code);   // short codes such as A/R, LOB codes stay as they are
    }

    /// <summary>Free text with code tokens inside ("B3 CLASSIFIED", "RISK_ACCEPTANCE APPROVED"): each code token becomes words.</summary>
    public static string Text(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return System.Text.RegularExpressions.Regex.Replace(text, @"\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\b|\b(?:PASS|FAIL|READY|CLASSIFIED|COMPLETE|RETURN|RESUBMIT|WITHDRAW|REVIEWED|FITS|HOLD|DECLINED|APPROVED|ACCEPTED|REJECTED|CLOSED|EXECUTED|WORKBENCH|ENTRA|SOD[0-9])\b",
            m => m.Index == 0 ? For(m.Value) : LowerFirst(For(m.Value)));
    }

    /// <summary>Lowercases the first letter unless the first word is an acronym (ITSM, LOB...).</summary>
    public static string LowerFirst(string s)
        => s.Length == 0 || (s.Length > 1 && char.IsUpper(s[1])) ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>"SOME_CODE" becomes "Some code" with acronyms kept.</summary>
    public static string Sentence(string code)
    {
        var s = code.Replace('_', ' ').ToLowerInvariant();
        if (s.Length == 0) return s;
        s = char.ToUpperInvariant(s[0]) + s[1..];
        foreach (var (lower, upper) in Acronyms) s = ReplaceWord(s, lower, upper);
        return s;
    }

    private static readonly (string, string)[] Acronyms =
    {
        ("lob", "LOB"), ("payg", "PAYG"), ("p3", "P3"), ("itsm", "ITSM"), ("raci", "RACI"), ("sla", "SLA"), ("kpi", "KPI"), ("sod", "SoD"),
        ("ppac", "PPAC"), ("m365", "Microsoft 365"), ("finops", "FinOps"), ("entra", "Entra"), ("csv", "CSV"), ("api", "API"), ("it", "IT"),
        ("es-it", "ES-IT"), ("microsoft", "Microsoft"), ("copilot", "Copilot"), ("teams", "Teams"), ("power", "Power"),
    };

    private static string ReplaceWord(string s, string lower, string upper)
    {
        var words = s.Split(' ');
        for (var i = 0; i < words.Length; i++)
            if (string.Equals(words[i], lower, StringComparison.OrdinalIgnoreCase)) words[i] = i == 0 && upper.Length > 0 ? char.ToUpperInvariant(upper[0]) + upper[1..] : upper;
        return string.Join(' ', words);
    }

    /// <summary>Audit action codes ("decision.record") as a sentence.</summary>
    public static string Action(string? action) => action switch
    {
        null or "" => "",
        "request.create" => "Created a request",
        "request.submit" => "Submitted a request",
        "request.edit" => "Edited a request",
        "request.withdraw" => "Withdrew a request",
        "task.claim" => "Claimed a task",
        "task.release" => "Released a task",
        "task.complete" => "Completed a task",
        "decision.record" => "Recorded a decision",
        "decision.view" => "Viewed decisions",
        "evidence.upload" => "Uploaded evidence",
        "evidence.download" => "Downloaded evidence",
        "evidence.view" => "Viewed evidence",
        "actas.start" => "Started acting as a persona",
        "actas.stop" => "Stopped acting as a persona",
        "audit.view" => "Viewed the audit trail",
        "audit.export" => "Exported the audit trail",
        "raci.draft" => "Drafted a RACI version",
        "raci.cell" => "Changed a RACI cell",
        "raci.approve" => "Approved a RACI version",
        "raci.activate" => "Activated a RACI version",
        "role.assign" => "Assigned a role",
        "role.end" => "Ended a role",
        "role.accept" => "Accepted a role",
        "intake.open" => "Opened intake",
        "intake.close" => "Closed intake",
        "sync.run" => "Ran a sync",
        "attestation.record" => "Recorded an attestation",
        "verification.record" => "Recorded a verification",
        "signin" => "Signed in",
        _ => Sentence(action.Replace('.', ' ')),
    };

    /// <summary>A setting value from a control template in words ("false" becomes "Off").</summary>
    public static string Value(string? value) => value switch
    {
        null or "" => "",
        "true" or "True" => "On",
        "false" or "False" => "Off",
        "none" or "None" or "null" => "None",
        _ => value,
    };

    /// <summary>One timeline line in plain words, or null when it says nothing (a state that did not change).</summary>
    public static string? Timeline(string eventType, string? stageCode, string? stageName, string? fromState, string? toState, string? text)
    {
        switch (eventType)
        {
            case "TRANSITION":
                var into = stageCode is null or "END" ? "Closed" : $"Moved to {stageCode} {stageName}".TrimEnd();
                var why = string.IsNullOrEmpty(text) ? "" : $" ({LowerFirst(For(text))})";
                var state = toState is not null && toState != fromState ? $" · {RequestStates.Label(toState)}" : "";
                return into + why + state;
            case "SUBMITTED":
                return "Submitted";
            case "EDITED" when fromState == toState:
                return "Edited";
            default:
                var head = For(eventType) + (stageCode is null ? "" : $" at {stageCode} {stageName}".TrimEnd());
                var change = toState is not null && toState != fromState ? $" · {RequestStates.Label(toState)}" : "";
                return head + change;
        }
    }
}
