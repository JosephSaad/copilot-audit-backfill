using System.Text.RegularExpressions;

namespace CopilotInsights.Infrastructure;

/// <summary>
/// The information icon's text for each field of the forms the pages write: a setting's own description, as
/// dash.Setting keeps it, and for the other fields the text here, by the field's name on its page, or by its name alone.
/// Every field had none: its help sat in paragraphs under the button, or nowhere.
/// </summary>
public static partial class FieldTips
{
    /// <summary>By "page|field" first, then by field: what a field takes, its unit, and what it changes.</summary>
    public static readonly IReadOnlyDictionary<string, string> Fixed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        /* Admin › Targets and assumptions: the price list and the suites. */
        ["/admin/assumptions|price"] = "What one unit costs a month (a credit: each), in the currency set in Admin › Access, after any discount your agreement gives.",
        ["tierFrom"] = "From this many seats every seat costs the volume price. Empty: no volume price.",
        ["tierPrice"] = "What each seat costs a month once the seats reach the number beside it.",
        ["dbGb"] = "Dataverse database capacity each seat adds to the tenant, in GB (Power Apps Premium: 0.25).",
        ["fileGb"] = "Dataverse file capacity each seat adds to the tenant, in GB (Power Apps Premium: 2).",
        ["spare"] = "The share of this license's seats kept unassigned on purpose, in percent. Empty: the default share above.",
        ["capacity"] = "Yes: bought for the Dataverse capacity or the volume price it brings, so its unassigned and idle seats are never counted as a saving.",
        ["includes"] = "What the suite already includes, so it is not counted twice: COPILOT, POWER_BI_PRO, separated by commas.",
        ["enabled"] = "Off leaves the suite out of Suite fit and Costs and savings.",
        ["minutes"] = "The minutes one interaction in this app saves. Empty: the default minutes.",
        ["/admin/assumptions|metric"] = "Adoption: license holders who used Copilot over all holders. Share of people active: everyone who used it over everyone. People active: a number of people.",
        ["/admin/assumptions|root"] = "A manager's sign-in (UPN): the target is for their part of the organization. Empty: the whole organization.",
        ["/admin/assumptions|value"] = "The target: a percentage up to 100, or a number of people for People active.",
        ["/admin/assumptions|start"] = "The day progress is measured from.",
        ["/admin/assumptions|due"] = "The day the target is due.",
        /* Leadership › Copilot reservations. */
        ["/leadership/reservations|orgId"] = "The organization's persistent chargeback ID: kept for good, so a reorganization does not move its billing.",
        ["/leadership/reservations|leader"] = "The leader's sign-in (UPN): everyone under them in the org chart counts with the organization. Empty: a pool with no one under it.",
        ["/leadership/reservations|costCenter"] = "The cost center the organization is billed to (optional).",
        ["/leadership/reservations|reserved"] = "Seats reserved for the organization: a whole number of Microsoft 365 Copilot add-on licenses.",
        ["/leadership/reservations|from"] = "The first day this applies. A new row ends the organization's open row the day before.",
        ["/leadership/reservations|to"] = "The last day this applies. Empty: until changed.",
        ["/leadership/reservations|end"] = "The last day this reservation is in effect: past months keep it, and the organization has none after it until a new row starts.",
        ["rate"] = "What one license costs a month after any discount, the rate the organizations are billed at.",
        ["baseline"] = "The unassigned add-on licenses counted when the purchase gate started (1,136 in the policy's example). Empty: no gate.",
        ["percent"] = "The share of the baseline's unassigned licenses to assign before more are bought, in percent (95 in the policy).",
        ["/leadership/reservations|upn"] = "The account's sign-in (UPN).",
        ["/leadership/reservations|category"] = "Why the Technology pool funds it: an executive, service, test, shared, migration or exception account.",
        ["/leadership/reservations|owner"] = "The person accountable for it.",
        ["/leadership/reservations|approval"] = "The approval's reference (a ticket or a change number).",
        ["/leadership/reservations|review"] = "The day the allocation is reviewed: it is an exception after it.",
        /* Governance › AI apps beyond Copilot: the resource map. */
        ["lob"] = "The line of business the resource belongs to.",
        ["env"] = "Production, development or test: guessed from the name (prod, dev, uat) until set here.",
        ["notes"] = "Anything worth knowing about the resource.",
        ["note"] = "A note, kept with the row.",
        /* Admin › Access. */
        ["PaygInteractionCost"] = "What one metered agent or work-grounded chat interaction costs without a license, in the currency above: 0.12 is 12 Copilot Credits at 0.01 each.",
        ["TimeZone"] = "The zone every time on the dashboard is shown in, and digests, alerts and extracts go out by. Default: the zone the data's days are cut in.",
        ["principal"] = "A person's name or sign-in (UPN), or a group as DOMAIN\\Group: the role reaches the group's members.",
        ["who"] = "A person's name or sign-in (UPN): what they may see, and why.",
        /* Admin › Alerts and digests. */
        ["/admin/notifications|name"] = "A name for the digest, shown in its subject line.",
        ["recipients"] = "Email addresses, separated by commas or semicolons.",
        ["url"] = "The webhook's address, https:// only: a Teams or Slack incoming webhook, or your own endpoint.",
        ["/admin/notifications|root"] = "A manager's sign-in (UPN): the digest covers their part of the organization. Empty: everyone.",
        ["/admin/notifications|upn"] = "A manager's name or sign-in (UPN).",
        /* Admin › Email. */
        ["ExoClientSecret"] = "The app registration's client secret, for OAuth sign-in to Exchange Online. Kept encrypted; leave it empty to keep the one saved.",
        ["SmtpPassword"] = "The SMTP account's password. Kept encrypted; leave it empty to keep the one saved.",
        ["/admin/email|to"] = "Where the test message goes.",
        /* Forecasting › Budget and renewal: the contracts. */
        ["/forecasting/budget|name"] = "The agreement's name, as Finance knows it.",
        ["seats"] = "The Microsoft 365 Copilot seats the agreement buys.",
        ["/forecasting/budget|price"] = "What one seat costs a month under this agreement, after its discount: the price that settles disagreements with Admin › Access.",
        ["/forecasting/budget|start"] = "The day the agreement started.",
        ["renewal"] = "The day the agreement renews.",
        /* Leadership › Credit chargeback: the routing register and the charge rate. */
        ["/leadership/credit-chargeback|environment"] = "The Power Platform environment, named exactly as the admin center's consumption report names it: that name matches its credits.",
        ["/leadership/credit-chargeback|path"] = "LoB Productivity or LoB Business Application: charged to a line of business. Enterprise Shared: funded centrally, never charged to one. Innovation: pilots and development, charged to its sponsor if it names one.",
        ["/leadership/credit-chargeback|orgId"] = "The line of business charged: its persistent Org ID, as Copilot reservations lists it. Required for the two LoB paths; empty for Enterprise Shared.",
        ["/leadership/credit-chargeback|costCenter"] = "The cost center the environment's credits are billed to (optional).",
        ["/leadership/credit-chargeback|owner"] = "The person accountable for the environment's spend: their sign-in or name (optional).",
        ["/leadership/credit-chargeback|policy"] = "The Power Platform billing policy the environment is linked to, as a label for reconciliation (optional).",
        ["/leadership/credit-chargeback|from"] = "The first day this route applies. A new route ends the environment's open one the day before, so past months keep theirs.",
        ["/leadership/credit-chargeback|to"] = "The last day this route applies. Empty: until changed.",
        ["/leadership/credit-chargeback|end"] = "The last day this route is in effect: past months keep it, and the environment is not routed after it until a new route starts.",
        ["/leadership/credit-chargeback|rate"] = "What one Copilot Credit is charged back at, after any discount, in the license currency (0.01 at list price). Empty: the price list's pay-as-you-go credit.",
        /* Forecasting › Credit commitment. */
        ["/forecasting/credit-commitment|name"] = "A name for the commitment, as Finance knows it (Copilot Credit P3 FY27).",
        ["/forecasting/credit-commitment|amount"] = "What was committed, in its currency: the commit units bought.",
        ["/forecasting/credit-commitment|currency"] = "The commitment's currency code (USD). Empty: the license currency.",
        ["/forecasting/credit-commitment|draw"] = "What one Copilot Credit draws from the commitment: 0.01 at list price, unless the agreement sets another.",
        ["/forecasting/credit-commitment|start"] = "The first day of the commitment's term.",
        ["/forecasting/credit-commitment|end"] = "The last day of the term: what is left after it is lost.",
        ["/forecasting/credit-commitment|alertDays"] = "Warn, on this page and by email to the license alert recipients, when the commitment is projected to run out this many days before its term ends, or to leave this many days' worth unused. 0 turns it off.",
        ["/forecasting/credit-commitment|scope"] = "The Azure scope it applies to (a billing account, subscription or resource group), as a label (optional).",
        /* Admin › Value names: the licenses' product names. */
        ["/admin/values|part"] = "The license's part number, as License pool shows it beside its name (SPE_E5, Microsoft_365_Copilot).",
        ["/admin/values|productName"] = "The name every page shows for that license (Microsoft 365 E5). Empty: remove the name, and show the part number.",
        /* Leadership › Copilot reservations. */
        ["/leadership/reservations|name"] = "The organization's name, as the business knows it.",
    };

    [GeneratedRegex(@"^\d+\.\d+\.\d+:\s*")]
    private static partial Regex Release();

    /// <summary>A setting's description as a tip: without the release it was added in.</summary>
    public static string FromDescription(string description) => Release().Replace(description.Trim(), "");

    /// <summary>The tips for one page: its settings' descriptions, then the fixed ones, the page's own first.</summary>
    public static IReadOnlyDictionary<string, string> For(string? path, IEnumerable<(string Name, string? Description)> settings)
    {
        var tips = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, description) in settings)
            if (!string.IsNullOrWhiteSpace(description)) tips[name] = FromDescription(description!);
        foreach (var (key, text) in Fixed)
        {
            var bar = key.IndexOf('|');
            if (bar < 0) tips.TryAdd(key, text);
            else if (string.Equals(key[..bar], path?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) tips[key[(bar + 1)..]] = text;
        }
        return tips;
    }
}
