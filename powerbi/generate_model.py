"""
Writes the Copilot Insights Power BI project (PBIP: a TMDL semantic model over the
bi views, and a report to build on) into powerbi/CopilotInsights.

    python powerbi/generate_model.py [--out <folder>] [--force]

Re-run after changing a bi view. Lineage tags are derived from names, so they do
not change between runs and Power BI keeps its bindings.

It writes only a project it wrote itself. generated-files.json lists every file
it wrote, with a hash; when the folder holds anything else (a report page, a
measure added in Desktop, a refresh policy or parameter changed there), it stops
and changes nothing. Before 2.4.12 it deleted both definition folders first,
so the report pages and model changes saved in the project were lost. Write the
new model to an empty folder with --out and bring the changes across in Power BI
Desktop, or pass --force to replace the project.
"""
import argparse, hashlib, json, os, shutil, sys, uuid

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "CopilotInsights")
NAME = "CopilotInsights"
MANIFEST = "generated-files.json"
T = "\t"

# The file types (2.4.6) were added to the model by hand, with tags of their own: kept, so Power BI keeps its bindings.
FIXED_TAGS = {
    ("FileTypes",): "23a36491-824a-4c07-a912-5a24a471ac97",
    ("FileTypes", "FileTypeKey"): "57dd709c-1eae-46fc-aa1c-df49ac910763",
    ("FileTypes", "FileType"): "ecad969d-ace3-4709-a5a9-94bb78059a2b",
    ("AccessedResources", "FileTypeKey"): "cb33e208-1785-44e9-8626-764519a1ac08",
    ("rel", "AccessedResources", "FileTypeKey"): "611b2c1f-5e94-4960-a75e-24fcc67a98bd",
}

def lt(*parts):  # stable lineage tags
    return FIXED_TAGS.get(parts) or str(uuid.uuid5(uuid.NAMESPACE_URL, "copilotinsights/" + "/".join(parts)))

def desc(text, indent):
    return "".join(f"{indent}/// {line}\n" for line in text.split("\n"))

# (name, tmdl dataType, options)  options: hidden, desc, fmt, sort, key
TABLES = {
    "Interactions": dict(
        desc="One row per Microsoft 365 Copilot prompt and response, from the Purview audit log. Contains no prompt or response text.",
        view="Interactions", incremental=True,
        cols=[
            ("ActivityHour", "dateTime", dict(hidden=True, fmt="General Date", desc="Hour of the interaction, in the reporting time zone. Used by incremental refresh.")),
            ("DateKey", "int64", dict(hidden=True)),
            ("HourOfDay", "int64", dict(desc="Hour of the day (0-23), in the reporting time zone.")),
            ("UserKey", "int64", dict(hidden=True)),
            ("AppKey", "int64", dict(hidden=True)),
            ("AgentKey", "int64", dict(hidden=True)),
            ("ModelKey", "int64", dict(hidden=True)),
            ("LicenseStatusKey", "int64", dict(hidden=True)),
            ("LicenseTypeKey", "int64", dict(hidden=True)),
            ("SensitivityLabelKey", "int64", dict(hidden=True)),
            ("PromptCount", "int64", dict(hidden=True)),
            ("MessageCount", "int64", dict(hidden=True)),
            ("AccessedResourceCount", "int64", dict(desc="Files, mail, sites and other resources Copilot read for this interaction.")),
            ("UsedWebSearch", "boolean", dict(desc="True when Copilot grounded the answer in a web search.")),
            ("JailbreakDetected", "boolean", dict(desc="True when Microsoft flagged a jailbreak attempt.")),
            ("PromptInjectionDetected", "boolean", dict(desc="True when Microsoft flagged a prompt injection.")),
            ("HasBlockedResource", "boolean", dict(desc="True when a resource Copilot tried to read was blocked.")),
            ("HasSensitivityLabel", "boolean", dict(desc="True when a resource Copilot read carried a sensitivity label.")),
            ("MemoryUpdated", "boolean", dict(desc="True when Copilot updated its memory of the person.")),
            ("DlpEvaluationDeferred", "boolean", dict(desc="True when the DLP check was deferred.")),
            ("CalledAnotherAgent", "boolean", dict(desc="True when the agent handed off to another agent.")),
            ("PremiumUse", "boolean", dict(desc="True when the interaction needed the Premium license: Copilot inside Word, Excel, PowerPoint, Outlook or Teams, or Copilot Chat reading work content rather than the web. The dashboard's train and revoke recommendations use the same rule.")),
            ("UseCaseKey", "int64", dict(hidden=True)),
        ],
        measures=[
            ("Interactions", "COUNTROWS ( Interactions )", "#,0",
             "Number of Copilot interactions (prompt and response pairs)."),
            ("Prompts", "SUM ( Interactions[PromptCount] )", "#,0",
             "Sum of the prompt count recorded on each interaction."),
            ("Active users", "DISTINCTCOUNT ( Interactions[UserKey] )", "#,0",
             "People with at least one Copilot interaction in the period."),
            ("Active licensed users",
             "VAR EndDate = [Period end]\n"
             "VAR HeldAtEnd = CALCULATETABLE ( VALUES ( LicensedDays[UserKey] ), Calendar[Date] = EndDate )\n"
             "RETURN\n"
             '    CALCULATE ( [Active users], LicenseStatuses[LicenseStatus] = "Licensed",\n'
             "        KEEPFILTERS ( TREATAS ( HeldAtEnd, Interactions[UserKey] ) ) )", "#,0",
             "People who used Copilot with a license in the period and still hold one at its end: always part of Licensed users, so adoption cannot pass 100% when licenses are removed mid-period (the dashboard's rule)."),
            ("Premium interactions", "CALCULATE ( [Interactions], Interactions[PremiumUse] = TRUE () )", "#,0",
             "Interactions that needed the Premium license."),
            ("Free interactions", "[Interactions] - [Premium interactions]", "#,0",
             "Interactions the free tier covers: web Copilot Chat, agents, and use without a license."),
            ("Premium share", "DIVIDE ( [Premium interactions], [Interactions] )", "0.0%",
             "The share of interactions that needed the Premium license."),
            ("Premium users", "CALCULATE ( [Active users], Interactions[PremiumUse] = TRUE () )", "#,0",
             "People who used a Premium feature in the period."),
            ("Rank by general use",
             "IF ( HASONEVALUE ( Users[UserPrincipalName] ) && [Interactions] > 0,\n"
             "    RANKX ( FILTER ( ALLSELECTED ( Users[UserPrincipalName] ), [Interactions] > 0 ), [Interactions] ) )", "#,0",
             "The person's rank by all their Copilot use, among the people in the current selection who used it (1 is the most)."),
            ("Rank by free use",
             "IF ( HASONEVALUE ( Users[UserPrincipalName] ) && [Interactions] > 0,\n"
             "    RANKX ( FILTER ( ALLSELECTED ( Users[UserPrincipalName] ), [Interactions] > 0 ), [Free interactions] ) )", "#,0",
             "The person's rank by use the free tier covers."),
            ("Rank by premium use",
             "IF ( HASONEVALUE ( Users[UserPrincipalName] ) && [Interactions] > 0,\n"
             "    RANKX ( FILTER ( ALLSELECTED ( Users[UserPrincipalName] ), [Interactions] > 0 ), [Premium interactions] ) )", "#,0",
             "The person's rank by use that needed the Premium license. A license holder low here is paying for what they do not use."),
            ("Web-grounded interactions",
             "CALCULATE ( [Interactions], Interactions[UsedWebSearch] = TRUE () )", "#,0",
             "Interactions Copilot grounded in a web search."),
        ]),
    "Sessions": dict(
        desc="One row per session: a person's prompts with less than 60 minutes between them, within one day in the reporting time zone.",
        view="Sessions",
        cols=[
            ("SessionKey", "int64", dict(hidden=True)),
            ("SessionStart", "dateTime", dict(fmt="General Date", desc="First prompt of the session, in the reporting time zone.")),
            ("SessionEnd", "dateTime", dict(fmt="General Date", desc="Last prompt of the session, in the reporting time zone.")),
            ("DateKey", "int64", dict(hidden=True)),
            ("UserKey", "int64", dict(hidden=True)),
            ("AppKey", "int64", dict(hidden=True, desc="The app of the session's first prompt.")),
            ("LicenseStatusKey", "int64", dict(hidden=True)),
            ("PromptCount", "int64", dict(hidden=True)),
            ("SpanMinutes", "double", dict(fmt="#,0.0", desc="Minutes from the first to the last prompt.")),
            ("IsMultiTurn", "boolean", dict(desc="True when the session has more than one prompt.")),
        ],
        measures=[
            ("Sessions", "COUNTROWS ( Sessions )", "#,0", "Number of Copilot sessions."),
            ("Prompts per session", "DIVIDE ( SUM ( Sessions[PromptCount] ), [Sessions] )", "#,0.0",
             "Average prompts in a session."),
            ("Multi-turn sessions", "CALCULATE ( [Sessions], Sessions[IsMultiTurn] = TRUE () )", "#,0",
             "Sessions with more than one prompt."),
        ]),
    "LicenseChanges": dict(
        desc="One row per Copilot SKU added to (+1) or removed from (-1) a person, from the directory audit log.",
        view="LicenseChanges",
        cols=[
            ("ChangedUtc", "dateTime", dict(fmt="General Date", desc="When the license changed, UTC.")),
            ("DateKey", "int64", dict(hidden=True)),
            ("UserKey", "int64", dict(hidden=True)),
            ("SkuKey", "int64", dict(hidden=True)),
            ("Change", "int64", dict(desc="+1 when the SKU was added, -1 when it was removed.")),
            ("ChangedBy", "string", dict(desc="Who made the change.")),
        ],
        measures=[
            ("Licenses added", "CALCULATE ( COUNTROWS ( LicenseChanges ), LicenseChanges[Change] = 1 )", "#,0",
             "Copilot SKUs assigned in the period."),
            ("Licenses removed", "CALCULATE ( COUNTROWS ( LicenseChanges ), LicenseChanges[Change] = -1 )", "#,0",
             "Copilot SKUs removed in the period."),
        ]),
    "LicensedDays": dict(
        desc="One row per person per day on which they held a Copilot license at the end of the day. Use for licensed-user counts on a date.",
        view="LicensedDays",
        cols=[
            ("DateKey", "int64", dict(hidden=True)),
            ("UserKey", "int64", dict(hidden=True)),
        ],
        # The end of the period is one day for every row, taken from Calendar, which no person filters
        # (2.4.12). Before, each measure took the last day on which the people in the row held a license,
        # so a department whose licenses were all removed on the 10th still counted them as licensed.
        measures=[
            ("Period end", "CALCULATE ( MAX ( Calendar[Date] ), KEEPFILTERS ( Calendar[IsLoaded] = TRUE () ) )", "yyyy-mm-dd",
             "The last day of the period the data covers: the day Licensed users and Adoption % count licenses on, the same for every row and the total (the dashboard's rule)."),
            ("Licensed users",
             "VAR EndDate = [Period end]\n"
             "RETURN CALCULATE ( DISTINCTCOUNT ( LicensedDays[UserKey] ), Calendar[Date] = EndDate )", "#,0",
             "People holding a Copilot license at the end of the period."),
            ("Adoption %", "DIVIDE ( [Active licensed users], [Licensed users] )", "0.0%",
             "Active licensed users divided by people holding a Copilot license at the end of the period."),
            ("Licensed users not using Copilot",
             "VAR EndDate = [Period end]\n"
             "VAR licensed = CALCULATETABLE ( VALUES ( LicensedDays[UserKey] ), Calendar[Date] = EndDate )\n"
             "VAR active = VALUES ( Interactions[UserKey] )\n"
             "RETURN COUNTROWS ( EXCEPT ( licensed, active ) )", "#,0",
             "People licensed at the end of the period with no Copilot interaction in it."),
        ]),
    "Calendar": dict(
        desc="Dates, with the Saturday-to-Friday week used throughout this model.",
        view="Calendar", date_table=True,
        cols=[
            ("DateKey", "int64", dict(hidden=True)),
            ("Date", "dateTime", dict(key=True, fmt="yyyy-mm-dd")),
            ("Year", "int64", {}),
            ("Quarter", "int64", {}),
            ("MonthNumber", "int64", dict(hidden=True)),
            ("MonthName", "string", dict(sort="MonthNumber")),
            ("YearMonth", "string", dict(desc="Year and month as yyyy-MM.")),
            ("DayOfWeekNumber", "int64", dict(hidden=True)),
            ("DayOfWeekName", "string", dict(sort="DayOfWeekNumber")),
            ("IsWeekday", "boolean", {}),
            ("WeekEnding", "dateTime", dict(fmt="yyyy-mm-dd", desc="The Friday that ends the date's Saturday-to-Friday week. Weeks in this model are always Saturday to Friday.")),
            ("WeekYear", "int64", {}),
            ("WeekNumber", "int64", dict(desc="Week of the year; week 1 ends on the year's first Friday.")),
            ("WeekLabel", "string", dict(desc="Week name, e.g. 2026-W38, for the Saturday-to-Friday week.")),
        ],
        # Calendar runs to the end of the year; the data ends on the day bi was last built. A calculated
        # column is worked out at refresh, outside row-level security, so it is the same for every viewer.
        calc_cols=[("IsLoaded", "boolean", "Calendar[DateKey] <= MAX ( LicensedDays[DateKey] )",
                    "True for the days the data covers, up to the last day licenses were counted. [Period end] takes its day from it.",
                    True)]),
    "Users": dict(
        desc="One row per person: directory attributes from on-premises Active Directory and today's Copilot license.",
        view="Users",
        cols=[
            ("UserKey", "int64", dict(hidden=True)),
            ("UserPrincipalName", "string", dict(desc="Sign-in name (UPN).")),
            ("DisplayName", "string", dict(hidden=True)),
            ("Division", "string", {}), ("Department", "string", {}), ("JobTitle", "string", {}),
            ("OfficeLocation", "string", {}), ("City", "string", {}), ("Country", "string", {}),
            ("CompanyName", "string", {}), ("EmployeeType", "string", {}),
            ("IsManager", "boolean", dict(desc="True when someone reports to this person.")),
            ("AccountEnabled", "boolean", {}),
            ("ManagerUpn", "string", dict(hidden=True)),
            ("ManagerName", "string", dict(desc="Direct manager's display name.")),
            ("OrgDepth", "int64", dict(desc="Number of managers above this person.")),
        ] + [(f"OrgLevel{i}", "string",
              dict(desc="Top of the management chain (a display name)." if i == 1 else
                   f"Manager {i} levels from the top of the chain (a display name)."))
             for i in range(1, 16)] + [
            ("DirectoryStatus", "string", dict(desc="Found when the person is in Active Directory; NotFound usually means they have left.")),
            ("CopilotLicenseToday", "string", dict(desc="Today's Copilot license: Licensed, Not licensed or Unknown. Not for past periods.")),
            ("CopilotSku", "string", {}),
            ("LicensedSinceUtc", "dateTime", dict(fmt="yyyy-mm-dd", desc="When the current Copilot license was assigned, UTC.")),
        ],
        calc_cols=[("User", "string",
                    "COALESCE ( Users[DisplayName], Users[UserPrincipalName] )",
                    "The person's name, or their sign-in name when the directory has none.", False)]),
    "Apps": dict(
        desc="The Microsoft 365 app a prompt was made in, and its category.",
        view="Apps",
        cols=[
            ("AppKey", "int64", dict(hidden=True)),
            ("App", "string", dict(desc="Where the prompt was made, e.g. Word, Outlook, Microsoft Teams, BizChat.")),
            ("AppCategory", "string", dict(desc="Copilot Chat or In-App Copilot.")),
        ]),
    "Agents": dict(
        desc="The Copilot Studio or Microsoft agent used, under its friendly name. (no agent) means Copilot itself. An agent keeps its row when a maker renames it: the name is a label on the agent, and all its history carries the latest one. Under the Organization role, only the agents the viewer's people used.",
        view="Agents",
        cols=[
            ("AgentKey", "int64", dict(hidden=True)),
            ("AgentId", "string", dict(desc="What identifies the agent in the audit log (its AppIdentity, else its AgentId). It does not change when the agent is renamed: use it to tell apart two agents with the same name.")),
            ("AgentName", "string", dict(desc="The agent's friendly name: the latest the audit log gave it (AgentName). Where the log gives none, a name read from the id: a Copilot Studio agent's schema name, or Microsoft's own name for one of its agents.")),
            ("AgentSource", "string", dict(desc="Copilot Studio, Microsoft, Agent ID or (no agent).")),
            ("NamedInAudit", "boolean", dict(desc="True when Agent Name is the audit log's own name for the agent; false when it was read from the id.")),
        ]),
    "Models": dict(
        desc="The AI model that answered.",
        view="Models",
        cols=[
            ("ModelKey", "int64", dict(hidden=True)),
            ("ModelProvider", "string", {}),
            ("ModelName", "string", {}),
        ]),
    "Skus": dict(
        desc="License SKUs that appear in the license history.",
        view="Skus",
        cols=[
            ("SkuKey", "int64", dict(hidden=True)),
            ("Sku", "string", {}),
            ("IsCopilot", "boolean", {}),
        ]),
    "LicenseTypes": dict(
        desc="The license Copilot recorded on the interaction (the license the records report).",
        view="LicenseTypes",
        cols=[("LicenseTypeKey", "int64", dict(hidden=True)), ("LicenseType", "string", {})]),
    "SensitivityLabels": dict(
        desc="Sensitivity labels of content Copilot read. Their names are in ValueNames and in the dashboard's Admin > Value names.",
        view="SensitivityLabels",
        cols=[("SensitivityLabelKey", "int64", dict(hidden=True)), ("LabelId", "string", {})]),
    "AccessedResources": dict(
        desc="One row per resource (file, mail, site, web page) Copilot read for an interaction.",
        view="AccessedResources", incremental=True,
        cols=[
            ("ActivityHour", "dateTime", dict(hidden=True, fmt="General Date")),
            ("DateKey", "int64", dict(hidden=True)), ("UserKey", "int64", dict(hidden=True)), ("AppKey", "int64", dict(hidden=True)),
            ("ResourceTypeKey", "int64", dict(hidden=True)), ("FileTypeKey", "int64", dict(hidden=True)), ("ResourceActionKey", "int64", dict(hidden=True)),
            ("SiteKey", "int64", dict(hidden=True)), ("SensitivityLabelKey", "int64", dict(hidden=True)),
            ("IsBlocked", "boolean", dict(desc="True when access to the resource was blocked.")),
            ("XPIADetected", "boolean", dict(desc="True when prompt injection was detected in the resource.")),
        ],
        measures=[("Resources read", "COUNTROWS ( AccessedResources )", "#,0", "Resources Copilot read."),
                  ("Blocked resources", "CALCULATE ( [Resources read], AccessedResources[IsBlocked] = TRUE () )", "#,0", "Resources Copilot was blocked from.")]),
    "ResourceTypes": dict(desc="The kind of resource read.", view="ResourceTypes",
        cols=[("ResourceTypeKey", "int64", dict(hidden=True)), ("ResourceType", "string", {})]),
    "FileTypes": dict(desc="What a resource read was: a file by its extension (docx, xlsx, pptx ...), anything else by its kind (2.4.6).", view="FileTypes",
        cols=[("FileTypeKey", "int64", dict(hidden=True)), ("FileType", "string", {})]),
    "ResourceActions": dict(desc="What Copilot did with the resource.", view="ResourceActions",
        cols=[("ResourceActionKey", "int64", dict(hidden=True)), ("ResourceAction", "string", {})]),
    "Sites": dict(desc="SharePoint and OneDrive sites Copilot read from. Under the Organization role, only the sites the viewer's people read.", view="Sites",
        cols=[("SiteKey", "int64", dict(hidden=True)), ("Site", "string", {}), ("Host", "string", {}),
              ("IsPersonal", "boolean", dict(desc="True for a OneDrive (personal) site."))]),
    "AgentEvents": dict(
        desc="Copilot Studio authoring: agents created, published, shared, changed and deleted, from Copilot Studio's audit records.",
        view="AgentEvents",
        cols=[
            ("RecordId", "string", dict(hidden=True)),
            ("EventUtc", "dateTime", dict(fmt="General Date", desc="When it happened, UTC.")),
            ("DateKey", "int64", dict(hidden=True)),
            ("Action", "string", dict(desc="Created, Published, Shared, Updated, Component or Deleted.")),
            ("Operation", "string", dict(desc="The audit operation, such as BotUpdateOperation-BotPublish.")),
            ("ActorKey", "int64", dict(hidden=True)),
            ("BotId", "string", dict(hidden=True)),
            ("BotSchemaName", "string", dict(desc="The agent's schema name.")),
            ("ComponentName", "string", {}), ("ComponentType", "string", {}),
        ],
        measures=[("Agent events", "COUNTROWS ( AgentEvents )", "#,0", "Authoring events."),
                  ("Agents published", 'CALCULATE ( DISTINCTCOUNT ( AgentEvents[BotSchemaName] ), AgentEvents[Action] = "Published" )', "#,0", "Agents published at least once in the period."),
                  ("Makers", "DISTINCTCOUNT ( AgentEvents[ActorKey] )", "#,0", "People who built or changed an agent.")]),
    "LicensePool": dict(
        desc="Licenses bought, assigned and available per SKU per day, from the tenant's subscriptions. Under the Organization role, only for viewers who see the whole organization.",
        view="LicensePool",
        cols=[
            ("SnapshotDate", "dateTime", dict(fmt="yyyy-mm-dd")),
            ("SkuPartNumber", "string", {}), ("IsCopilot", "boolean", {}),
            ("Bought", "int64", {}), ("Assigned", "int64", {}), ("Available", "int64", {}),
            ("InGracePeriod", "int64", {}), ("Suspended", "int64", {}),
        ],
        measures=[("Copilot seats bought", "CALCULATE ( SUM ( LicensePool[Bought] ), LicensePool[IsCopilot] = TRUE (), LASTDATE ( LicensePool[SnapshotDate] ) )", "#,0", "Copilot seats bought, on the last day in the filter."),
                  ("Copilot seats assigned", "CALCULATE ( SUM ( LicensePool[Assigned] ), LicensePool[IsCopilot] = TRUE (), LASTDATE ( LicensePool[SnapshotDate] ) )", "#,0", "Copilot seats assigned, on the last day in the filter.")]),
    "DepartmentWeek": dict(
        desc="Use by department and week. Figures for departments under the minimum group size are blank (Hidden is true), so this table can be shared more widely than the person-level tables: through the Departments role, which shows it and no one's own figures. Under the Organization role, only for viewers who see the whole organization.",
        view="DepartmentWeek",
        cols=[("WeekEnding", "dateTime", dict(fmt="yyyy-mm-dd")), ("Department", "string", {}),
              ("ActivePeople", "int64", {}), ("Interactions", "int64", {}), ("Sessions", "int64", {}), ("Hidden", "boolean", {})]),
    "ValueNames": dict(
        desc="Readable names administrators gave the codes in the audit records (labels, apps, agents, license types), from the dashboard's Admin > Value names. Under the Organization role, only for viewers who see the whole organization.",
        view="ValueNames",
        cols=[("TableName", "string", {}), ("ColumnName", "string", {}), ("ColumnLabel", "string", {}), ("Value", "string", {}),
              ("Name", "string", {}), ("Records", "int64", {}), ("FirstSeenUtc", "dateTime", dict(fmt="yyyy-mm-dd")), ("LastSeenUtc", "dateTime", dict(fmt="yyyy-mm-dd"))]),
    "UseCases": dict(
        desc="What an interaction was for, from its app, whether it searched the web and whether an agent answered: the rules in the dashboard's Admin > Classifications and events.",
        view="UseCases",
        cols=[("UseCaseKey", "int64", dict(hidden=True)), ("App", "string", dict(hidden=True)),
              ("WebGrounded", "boolean", dict(hidden=True)), ("WithAgent", "boolean", dict(hidden=True)),
              ("UseCase", "string", dict(desc="Email, Meetings and chats, Writing documents, Analyzing data, Research on the web..."))]),
    "JobFamilies": dict(
        desc="Each person's job family, from their job title and the rules in the dashboard's Admin > Classifications and events.",
        view="JobFamilies",
        cols=[("UserKey", "int64", dict(hidden=True)), ("JobFamily", "string", dict(desc="(no family) when no rule matches the title."))]),
    "EventMarkers": dict(
        desc="Events marked on the dashboard's trends (rollout waves, training, outages, policy changes), for annotating charts.",
        view="EventMarkers",
        cols=[("Day", "dateTime", dict(fmt="yyyy-mm-dd")), ("DateKey", "int64", dict(hidden=True)),
              ("Label", "string", {}), ("Kind", "string", {})]),
    "OrgSecurity": dict(
        desc="Who may see whom: each viewer's sign-in and the people at or under them in the org chart. Used by the Organization role, which shows each viewer only their own rows.",
        view="OrgSecurity", hidden=True,
        cols=[("ViewerUpn", "string", dict(hidden=True)), ("UserKey", "int64", dict(hidden=True)),
              ("WholeOrganization", "boolean", dict(hidden=True, desc="True when the viewer sees the whole organization: the top two levels, and anyone granted Organization."))]),
    "LicenseStatuses": dict(
        desc="Whether the person held a Copilot license when the interaction or session happened.",
        view="LicenseStatuses",
        cols=[
            ("LicenseStatusKey", "int64", dict(hidden=True)),
            ("LicenseStatus", "string", dict(desc="Licensed, Not licensed, or Unknown (no record either way; never the same as Not licensed).")),
        ]),
}

# Row-level security reaches Sites and Agents back through the facts, so a viewer sees only the sites their
# people read and the agents they used (2.4.12; before, every role member could list every site and OneDrive
# in the tenant). Each fact carries this for one relationship only, as Power BI requires.
SECURED_BOTH_WAYS = "\tcrossFilteringBehavior: bothDirections\n\tsecurityFilteringBehavior: bothDirections\n"

RELATIONSHIPS = [
    ("Interactions", "DateKey", "Calendar"), ("Interactions", "UserKey", "Users"),
    ("Interactions", "AppKey", "Apps"), ("Interactions", "AgentKey", "Agents", "AgentKey", SECURED_BOTH_WAYS),
    ("Interactions", "ModelKey", "Models"), ("Interactions", "LicenseStatusKey", "LicenseStatuses"),
    ("Sessions", "DateKey", "Calendar"), ("Sessions", "UserKey", "Users"),
    ("Sessions", "AppKey", "Apps"), ("Sessions", "LicenseStatusKey", "LicenseStatuses"),
    ("LicenseChanges", "DateKey", "Calendar"), ("LicenseChanges", "UserKey", "Users"),
    ("LicenseChanges", "SkuKey", "Skus"),
    ("LicensedDays", "DateKey", "Calendar"), ("LicensedDays", "UserKey", "Users"),
    ("Interactions", "LicenseTypeKey", "LicenseTypes"), ("Interactions", "SensitivityLabelKey", "SensitivityLabels"),
    ("AccessedResources", "DateKey", "Calendar"), ("AccessedResources", "UserKey", "Users"), ("AccessedResources", "AppKey", "Apps"),
    ("AccessedResources", "ResourceTypeKey", "ResourceTypes"), ("AccessedResources", "FileTypeKey", "FileTypes"),
    ("AccessedResources", "ResourceActionKey", "ResourceActions"),
    ("AccessedResources", "SiteKey", "Sites", "SiteKey", SECURED_BOTH_WAYS), ("AccessedResources", "SensitivityLabelKey", "SensitivityLabels"),
    ("AgentEvents", "DateKey", "Calendar"), ("AgentEvents", "ActorKey", "Users", "UserKey"),
    ("Interactions", "UseCaseKey", "UseCases"), ("EventMarkers", "DateKey", "Calendar"),
    # One job family per person: one-to-one, filtering both ways, so the family slices every fact through Users.
    ("JobFamilies", "UserKey", "Users", "UserKey", "\tfromCardinality: one\n\tcrossFilteringBehavior: bothDirections\n"),
]


def m_source(view, incremental):
    lines = ["let",
             "    Source = Sql.Database(SqlServer, SqlDatabase),",
             f'    Data = Source{{[Schema = "bi", Item = "{view}"]}}[Data]']
    if incremental:
        lines[-1] += ","
        lines.append("    Filtered = Table.SelectRows(Data, each [ActivityHour] >= RangeStart and [ActivityHour] < RangeEnd)")
        lines += ["in", "    Filtered"]
    else:
        lines += ["in", "    Data"]
    return lines


def table_tmdl(name, t):
    o = []
    o.append(desc(t["desc"], ""))
    o.append(f"table {name}\n")
    o.append(f"{T}lineageTag: {lt(name)}\n")
    if t.get("hidden"):
        o.append(f"{T}isHidden\n")
    if t.get("date_table"):
        o.append(f"{T}dataCategory: Time\n")
    o.append("\n")
    if t.get("incremental"):
        # The refresh policy is part of the model (2.4.12): before, it was set by hand in Desktop, and a
        # model without it loaded only the parameters' fixed range, so nothing after it.
        o.append(f"{T}refreshPolicy\n")
        o.append(f"{T*2}policyType: basic\n")
        o.append(f"{T*2}rollingWindowGranularity: year\n")
        o.append(f"{T*2}rollingWindowPeriods: 2\n")
        o.append(f"{T*2}incrementalGranularity: day\n")
        o.append(f"{T*2}incrementalPeriods: 4\n")
        o.append(f"{T*2}sourceExpression =\n")
        for ln in m_source(t["view"], True):
            o.append(f"{T*4}{ln}\n")
        o.append("\n")
    for mname, expr, fmt, mdesc in t.get("measures", []):
        o.append(desc(mdesc, T))
        qn = f"'{mname}'" if any(c in mname for c in " .=:'%-") else mname
        lines = expr.split("\n")
        if len(lines) == 1:
            o.append(f"{T}measure {qn} = {expr}\n")
        else:
            o.append(f"{T}measure {qn} =\n")
            for ln in lines:
                o.append(f"{T*3}{ln}\n")
        o.append(f"{T*2}formatString: {fmt}\n")
        o.append(f"{T*2}lineageTag: {lt(name, 'm', mname)}\n\n")
    for cname, dtype, opt in t["cols"]:
        if opt.get("desc"):
            o.append(desc(opt["desc"], T))
        o.append(f"{T}column {cname}\n")
        o.append(f"{T*2}dataType: {dtype}\n")
        if opt.get("fmt"):
            o.append(f"{T*2}formatString: {opt['fmt']}\n")
        elif dtype == "int64":
            o.append(f"{T*2}formatString: 0\n")
        if opt.get("key"):
            o.append(f"{T*2}isKey\n")
        if opt.get("hidden"):
            o.append(f"{T*2}isHidden\n")
        o.append(f"{T*2}lineageTag: {lt(name, cname)}\n")
        o.append(f"{T*2}summarizeBy: none\n")
        o.append(f"{T*2}sourceColumn: {cname}\n")
        if opt.get("sort"):
            o.append(f"{T*2}sortByColumn: {opt['sort']}\n")
        o.append("\n")
    for cname, dtype, expr, cdesc, chidden in t.get("calc_cols", []):
        o.append(desc(cdesc, T))
        o.append(f"{T}column {cname} = {expr}\n")
        o.append(f"{T*2}dataType: {dtype}\n")
        if chidden:
            o.append(f"{T*2}isHidden\n")
        o.append(f"{T*2}lineageTag: {lt(name, cname)}\n")
        o.append(f"{T*2}summarizeBy: none\n\n")
    o.append(f"{T}partition {name} = m\n")
    o.append(f"{T*2}mode: import\n")
    o.append(f"{T*2}source =\n")
    for ln in m_source(t["view"], t.get("incremental")):
        o.append(f"{T*4}{ln}\n")
    o.append("\n")
    o.append(f"{T}annotation PBI_ResultType = Table\n")
    return "".join(o)


def long_path(path):
    # Long paths on Windows (a deep checkout passes 260 characters).
    if os.name == "nt" and not path.startswith("\\\\?\\"):
        return "\\\\?\\" + os.path.abspath(path)
    return path


def write(path, text):
    path = long_path(path)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\r\n") as f:
        f.write(text)


def digest(data):
    # Line endings aside: git checks the project out with either.
    return hashlib.sha256(data.replace(b"\r\n", b"\n")).hexdigest()


# Who sees the whole organization, from the viewer's own rows of OrgSecurity only, so the answer does not
# depend on whether other tables' filters apply while it is worked out.
WHOLE_ORGANIZATION = ("NOT ISEMPTY ( CALCULATETABLE ( OrgSecurity, OrgSecurity[ViewerUpn] = USERPRINCIPALNAME (), "
                      "OrgSecurity[WholeOrganization] = TRUE () ) )")


def project_files():
    """Every file of the project, by its path under the project folder (parts joined with /)."""
    files = {}
    sm, rp = f"{NAME}.SemanticModel", f"{NAME}.Report"
    d, rd = f"{sm}/definition", f"{rp}/definition"

    files[f"{NAME}.pbip"] = json.dumps({
        "$schema": "https://developer.microsoft.com/json-schemas/fabric/pbip/pbipProperties/1.0.0/schema.json",
        "version": "1.0",
        "artifacts": [{"report": {"path": f"{NAME}.Report"}}],
        "settings": {"enableAutoRecovery": True}}, indent=2) + "\n"
    files[f"{sm}/definition.pbism"] = json.dumps({
        "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/semanticModel/definitionProperties/1.0.0/schema.json",
        "version": "4.2", "settings": {}}, indent=2) + "\n"
    files[".gitignore"] = "**/.pbi/localSettings.json\n**/.pbi/cache.abf\n"

    files[f"{d}/database.tmdl"] = "database\n\tcompatibilityLevel: 1600\n"
    model = ["model Model\n", "\tculture: en-US\n", "\tdefaultPowerBIDataSourceVersion: powerBI_V3\n",
             "\tdiscourageImplicitMeasures\n", "\tsourceQueryCulture: en-US\n", "\tdataAccessOptions\n",
             "\t\tlegacyRedirects\n", "\t\treturnErrorValuesAsNull\n\n",
             "annotation __PBI_TimeIntelligenceEnabled = 0\n\n",
             'annotation PBI_QueryOrder = ["SqlServer","SqlDatabase","RangeStart","RangeEnd",'
             + ",".join(f'"{n}"' for n in TABLES) + "]\n\n",
             "annotation PBI_ProTooling = [\"DevMode\"]\n\n"]
    model += [f"ref table {n}\n" for n in TABLES]
    model += ["\nref role Organization\n", "ref role Departments\n"]
    files[f"{d}/model.tmdl"] = "".join(model)

    def param(name, value, typ, text):
        return (desc(text, "") +
                f"expression {name} = {value} meta [IsParameterQuery=true, Type=\"{typ}\", IsParameterQueryRequired=true]\n"
                f"\tlineageTag: {lt('param', name)}\n\n"
                f"\tannotation PBI_ResultType = {'Text' if typ == 'Text' else 'DateTime'}\n\n")
    # The range covers everything (2.4.12): it ended on 1 January 2027, after which a copy with no refresh policy
    # loaded no new interactions and said nothing. The service replaces both values with the policy's own ranges.
    files[f"{d}/expressions.tmdl"] = (
        param("SqlServer", '"localhost"', "Text", "SQL Server instance holding the Copilot database (for Azure SQL, its server name).") +
        param("SqlDatabase", '"CopilotAudit"', "Text", "The audit database with the bi schema.") +
        param("RangeStart", "#datetime(2000, 1, 1, 0, 0, 0)", "DateTime",
              "Incremental refresh: start of the range Power BI Desktop loads. The service replaces it with each partition's own start.") +
        param("RangeEnd", "#datetime(2100, 1, 1, 0, 0, 0)", "DateTime",
              "Incremental refresh: end of the range Power BI Desktop loads (exclusive). The service replaces it with each partition's own end."))

    rel = []
    for r in RELATIONSHIPS:
        f, c, dim = r[:3]
        to = r[3] if len(r) > 3 else c
        extra = r[4] if len(r) > 4 else ""
        rel.append(f"relationship {lt('rel', f, c)}\n{extra}\tfromColumn: {f}.{c}\n\ttoColumn: {dim}.{to}\n\n")
    files[f"{d}/relationships.tmdl"] = "".join(rel)

    for n, t in TABLES.items():
        files[f"{d}/tables/{n}.tmdl"] = table_tmdl(n, t)

    # Row-level security from the org chart: a viewer sees the people at or under them (bi.OrgSecurity), and the
    # sites and agents come through the facts (SECURED_BOTH_WAYS). Before 2.4.12 the role filtered Users alone, so
    # every member could read OrgSecurity whole (every viewer's sign-in and who they see), every site and OneDrive
    # in the tenant, and the whole-organization tables, even a member outside the org chart.
    files[f"{d}/roles/Organization.tmdl"] = (
        desc("Each viewer sees the people at or under them in the org chart, as the dashboard does, and only what those people did:\n"
             "the sites they read and the agents they used. LicensePool, ValueNames and DepartmentWeek hold figures for the whole\n"
             "organization, so they show only to viewers who see the whole organization.\n"
             "Add the report's viewers (or an Entra group) to this role in the Power BI service.", "") +
        "role Organization\n\tmodelPermission: read\n\n"
        "\ttablePermission Users = Users[UserKey] IN CALCULATETABLE ( VALUES ( OrgSecurity[UserKey] ), OrgSecurity[ViewerUpn] = USERPRINCIPALNAME () )\n\n"
        "\ttablePermission OrgSecurity = OrgSecurity[ViewerUpn] = USERPRINCIPALNAME ()\n\n"
        + "".join(f"\ttablePermission {n} = {WHOLE_ORGANIZATION}\n\n" for n in ("LicensePool", "ValueNames", "DepartmentWeek")).rstrip("\n") + "\n")
    # The department figures alone, for sharing more widely, and in privacy mode, where bi.OrgSecurity is empty
    # and the Organization role sees nobody.
    files[f"{d}/roles/Departments.tmdl"] = (
        desc("Department figures only: DepartmentWeek, where a group under the minimum size is blank, with the calendar and the\n"
             "reference tables. No one's own figures. For sharing more widely, and in privacy mode, where the Organization role sees nobody.", "") +
        "role Departments\n\tmodelPermission: read\n\n"
        + "".join(f"\ttablePermission {n} = FALSE ()\n\n" for n in ("Users", "OrgSecurity", "LicensePool", "ValueNames")).rstrip("\n") + "\n")

    # Report: one blank page, PBIR format.
    files[f"{rp}/definition.pbir"] = json.dumps({
        "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/report/definitionProperties/2.0.0/schema.json",
        "version": "4.0",
        "datasetReference": {"byPath": {"path": f"../{NAME}.SemanticModel"}}}, indent=2) + "\n"
    files[f"{rd}/version.json"] = json.dumps({
        "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/versionMetadata/1.0.0/schema.json",
        "version": "2.0.0"}, indent=2) + "\n"
    files[f"{rd}/report.json"] = json.dumps({
        "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/report/3.0.0/schema.json",
        "themeCollection": {"customTheme": {"name": "CopilotInsights.json", "reportVersionAtImport": {"visual": "2.4.0", "report": "3.0.0", "page": "2.3.0"}, "type": "RegisteredResources"}},
        "resourcePackages": [{"name": "RegisteredResources", "type": "RegisteredResources",
                              "items": [{"name": "CopilotInsights.json", "path": "CopilotInsights.json", "type": "CustomTheme"}]}]},
        indent=2) + "\n"
    files[f"{rd}/pages/pages.json"] = json.dumps({
        "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/pagesMetadata/1.0.0/schema.json",
        "pageOrder": ["overview"], "activePageName": "overview"}, indent=2) + "\n"
    files[f"{rd}/pages/overview/page.json"] = json.dumps({
        "$schema": "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/page/2.0.0/schema.json",
        "name": "overview", "displayName": "Overview", "displayOption": "FitToPage",
        "height": 720, "width": 1280}, indent=2) + "\n"
    # The dashboard's categorical colors, so charts look the same in both.
    files[f"{rp}/StaticResources/RegisteredResources/CopilotInsights.json"] = json.dumps({
        "name": "Copilot Insights",
        "dataColors": ["#2a78d6", "#eb6834", "#1baf7a", "#eda100", "#e87ba4", "#008300", "#4a3aa7", "#e34948"],
        "good": "#0ca30c", "neutral": "#eda100", "bad": "#d03b3b"}, indent=2) + "\n"
    return files


def existing_files(out):
    """The files in the project folder now, with their hashes: all but the manifest and folders whose name
    starts with a dot (Desktop's .pbi, with its local settings and cache, is not part of the project)."""
    found = {}
    base = long_path(out)
    if not os.path.isdir(base):
        return found
    for dirpath, dirs, names in os.walk(base):
        dirs[:] = [x for x in dirs if not x.startswith(".")]
        for n in names:
            full = os.path.join(dirpath, n)
            rel = full[len(base):].lstrip("\\/").replace("\\", "/")
            if rel == MANIFEST:
                continue
            with open(full, "rb") as f:
                found[rel] = digest(f.read())
    return found


def foreign_files(out, files):
    """What in the folder this script did not write, or was changed after it wrote it; and its last manifest."""
    try:
        with open(long_path(os.path.join(out, MANIFEST)), encoding="utf-8") as f:
            written = json.load(f).get("files", {})
    except (OSError, ValueError, AttributeError):
        written = {}
    new = {rel: digest(text.encode("utf-8")) for rel, text in files.items()}
    foreign = [rel for rel, h in sorted(existing_files(out).items())
               if written.get(rel) != h and new.get(rel) != h]
    return foreign, written


def main(argv=None):
    p = argparse.ArgumentParser(description="Writes the Copilot Insights Power BI project.")
    p.add_argument("--out", default=OUT, help="the project folder (default: CopilotInsights beside this script)")
    p.add_argument("--force", action="store_true",
                   help="replace the project even where it holds files this script did not write, which are lost")
    a = p.parse_args(argv)
    out = os.path.abspath(a.out)
    files = project_files()
    foreign, written = foreign_files(out, files)
    if foreign and not a.force:
        print(f"{out} holds files this script did not write, or changed after it wrote them:", file=sys.stderr)
        for rel in foreign[:20]:
            print("  " + rel, file=sys.stderr)
        if len(foreign) > 20:
            print(f"  and {len(foreign) - 20} more", file=sys.stderr)
        print("Nothing was changed. To keep that work, write the new model to an empty folder with --out <folder>\n"
              "and bring its changes into this project in Power BI Desktop. To replace the project, losing that work,\n"
              "run it again with --force.", file=sys.stderr)
        return 1
    base = long_path(out)
    if a.force:
        for sub in (f"{NAME}.SemanticModel", f"{NAME}.Report"):
            if os.path.isdir(os.path.join(base, sub, "definition")):
                shutil.rmtree(os.path.join(base, sub, "definition"))
    else:
        # A file an earlier run wrote that this one does not (a table taken out of the model) goes too.
        for rel in written:
            path = os.path.join(base, *rel.split("/"))
            if rel not in files and os.path.isfile(path):
                os.remove(path)
    for rel, text in files.items():
        write(os.path.join(out, *rel.split("/")), text)
    write(os.path.join(out, MANIFEST), json.dumps({
        "about": "The files powerbi/generate_model.py wrote, with their SHA-256 (line endings aside). "
                 "It replaces only these, and only while they are unchanged.",
        "files": {rel: digest(text.encode("utf-8")) for rel, text in sorted(files.items())}}, indent=2) + "\n")
    print("written to", out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
