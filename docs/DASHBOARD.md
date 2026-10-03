# Copilot Insights dashboard

A web dashboard over the Copilot audit data. Everyone signs in with their Windows account and sees the part of the organization they are entitled to. Executives and anyone granted it see everything; a manager sees their own people; everyone else sees their own use. Pages are grouped by persona. A web-based admin module holds every setting: access, email, alerts, digests and the webhook, targets and assumptions, privacy and retention, reference data and value names, classifications and events, scheduled extracts, training records, directory quality, health and the audit log. One dashboard can span several Microsoft 365 tenants.

It is an ASP.NET Core 10 (Razor Pages) site hosted in IIS on Windows Server 2022 or later. It reads the Copilot database the pipeline already fills: the `bi` star schema, plus its own `dash` schema of settings, roles, the org chart and daily rollups. It has no other data store.

**Read-only towards Microsoft 365, unless someone approves a change.** Nothing in the dashboard or the pipeline changes anything in Microsoft 365, Entra or Azure: license changes are files and scripts an administrator reviews and runs with their own rights. What it does write, and where:

| It writes | Where | Set by |
|---|---|---|
| Its settings, roles, names, rules and logs | Its own `dash` tables | Administrators, in the Admin pages |
| Email | Exchange Online or the SMTP server set in Admin › Email | Alerts and digests |
| Alert posts | The one webhook address set in Admin › Alerts and digests | Alerts |
| CSV files of aggregates | The folders set in Admin › Scheduled extracts | Scheduled extracts |
| License files and scripts | The administrator's browser, as downloads | License exports |

The roadmap's later milestones (M13 to M15) would add changes in Microsoft 365 itself, such as deploying an approved DLP policy. They would use a separate app registration whose credentials the pipeline does not hold, and act only on a change a second person has approved.

**Status.** Released in 2.2.0; the operations features (alerts, digests, the admin settings, value names, the data API, triage, planning, classifications, extracts, multi-tenancy and the pages listed below) in 2.3.0. Every page, the security model, the admin changes and the rollups were tested against a synthetic 2,317-person demo database, a 40-million-interaction scale database and the SQL integration suite; email was tested against a local SMTP server requiring authentication, on ports 2525 (STARTTLS) and 4465 (implicit TLS); every page's layout was checked in a browser at 1440 and 1024 pixels wide for clipped, overlapping or shortened labels. It has not yet run on a Windows Server under IIS. See [Not yet proven](#not-yet-proven).

## Contents

- [What each persona gets](#what-each-persona-gets)
- [Who sees what](#who-sees-what)
- [Periods](#periods)
- [Premium use and the license rules](#premium-use-and-the-license-rules)
- [Events and hover text](#events-and-hover-text)
- [More than one tenant](#more-than-one-tenant)
- [Long lists and exports](#long-lists-and-exports)
- [Email, alerts and digests](#email-alerts-and-digests)
- [Targets, value, training and champions](#targets-value-training-and-champions)
- [Privacy mode and retention](#privacy-mode-and-retention)
- [Value names](#value-names)
- [The data API](#the-data-api)
- [Installing it on IIS](#installing-it-on-iis)
- [Enrolling the first administrator](#enrolling-the-first-administrator)
- [Running it](#running-it)
- [Trying it on a demo database](#trying-it-on-a-demo-database)
- [Not yet proven](#not-yet-proven)

## What each persona gets

**Finding a report (2.4.9).** The list on the left shows the sections, folded, with the one you are in open and your page marked. Selecting a section opens its reports under it, and selecting it again folds them; nothing else in the list moves, and each page starts with only its own section open. The list stays on screen while a long report scrolls. *About these reports*, under an open section's reports, opens a panel beside it with all of them under their headings (in Licensing: inventory and pool, cost savings, policy breaches, changes ahead), each with a sentence on what it answers and the role it needs; Escape, the close button or a click elsewhere closes it. **Your most used**, the first section, lists up to ten reports you opened most in the last 90 days, once you have opened three: it is counted from the audit log of page views the dashboard already keeps, worked out once a day, and nothing is saved or set up for it. *Find a report* at the top narrows the list as you type, by a report's name, by what it answers, or by its section's name or audience; it says how many match, the arrow keys walk them and Enter opens the first; pressing / on any page puts the cursor there. Resting the pointer on a report's link, or reaching it with the keyboard, shows the same sentence beside the list. The start page is the full index: every report you may open, under the same headings, with the same sentences. The foot of each report offers the report before it in its section and the one after (2.4.10), each with its sentence, and keeps the period and the part of the organization you are looking at. Reports you may not open, and reports an administrator has turned off, are in none of them. An address with no page behind it (mistyped, or a bookmark to a page since moved) is answered with *Page not found* (2.4.10): the address, the reports nearest to it among those you can open, and the way to the start page.

**Seven sections belong to a role (2.4.2):** Adoption (the **Adoption** role), Licensing and Forecasting (**Licensing**), Security (**Security**), Compliance (**Compliance**), Agents (**Agents**) and Enablement (**Enablement**). Without the role, the whole section is hidden from the navigation and the start page, its aggregate pages included, and its pages and API calls are refused; this holds for the top of the organization too. Leadership, Regions, People, App owners and Governance are open to everyone who may use the dashboard, for their own part of the organization. Who may use it at all can be limited to the top levels of the organization ([Who sees what](#who-sees-what)).

Pages without a role mark show aggregates only: counts, rates and trends for the viewer's scope, broken down one level at a time. Pages marked with a role list named people. They need that role, and even then they show only people inside the viewer's own scope. The navigation and the start page list only the pages the viewer can open: someone without a role never sees its section, an administrator's Admin pages are invisible to everyone else, and a persona with nothing open to the viewer is left out.

| Persona | Page | What it answers | Grain | Named people |
|---|---|---|---|---|
| Leadership | Overview | People using Copilot, licensed, adoption, active in the last 7 days; trends; adoption by the next level down | Scope, then one level down | No |
| | Scorecard | This period against the one before it: people, adoption split into Premium (license holders who used what the license pays for) and free only (those who used only what is free without it), licenses, interactions and sessions with a follow-up, and each unit one level down with its change | Scope, one level down | No |
| | Organization | Each unit one level down side by side: people, licensed, active, adoption, days active, interactions, share of sessions with a follow-up, a trend line; heatmap of share active per week | One level down | No |
| | Apps and agents | Interactions by app over time, Copilot Chat and agent share, most-used agents | Scope | No |
| | Value and time saved | Estimated hours saved (interactions times the minutes each app saves) and their worth, by app and department, against the license cost | Scope, app, department | No |
| | Use cases | What Copilot is used for (email, meetings, writing, analysis, research, agents...) from the app, web search and agent, by the rules in Admin › Classifications and events; over time and by department | Scope, department | No |
| | Board pack | One printable page for the board: use and adoption against the period before, the trend, targets, estimated value against license cost, licenses and risk signals | Scope | No |
| | Group overview | With several tenants: each tenant's people, adoption and interactions side by side, for the tenants the viewer can see | Tenant | No |
| Adoption | Funnel and trend | Licensed → started → active → active this week → followed up on an answer; first-time users over time | Scope | No |
| | Usage by license | For licensed, unlicensed or everyone you can see: people, active and inactive; use by app and by the file type read (docx, xlsx, pptx ..., or a mail, a Teams message); the highest and lowest users; minutes used (the span of each conversation, first prompt to last) by person, by app, with averages for Outlook, Teams and Copilot Chat, and every conversation, downloadable; interactions by app over the period with each app's lowest, highest and mean point; people by days active; the last database update and the latest audit record | Person | **Adoption** |
| | Retention and habit | Of everyone active in a week, how many come back in each of the next eight weeks; how many of the last twelve weeks people used it | Weekly cohorts | No |
| | Retention by license month | Of the people licensed each month, the share active in weeks 1 to 12 after; time from license to first use | Monthly cohorts | No |
| | Departments | Licensed, active and adoption per AD department; small departments combined | Department | No |
| | Leaderboard | Departments (or divisions, companies, countries, offices) ranked by adoption, with the change on the period before and the most improved | Attribute | No |
| | Targets | Each adoption target against where it started, where it is now and where it should be by now | Target | No |
| | New joiners | Days from getting a license (or joining, with an Entra hire date) to first use; how many have not started; by department | Scope, department | No |
| | Drops and spikes | Days in the last two weeks when a department's use was far from its usual level for that weekday | Department, day | No |
| | Not started | People licensed 14 days or more who have never used Copilot | Person | **Adoption** |
| Licensing | License inventory | Licenses held over time, assigned and removed, by SKU, idle licenses by unit | Scope, SKU, unit | No |
| | Unused licenses | People holding a license with no use for 7, 14, 30, 60 or 90 days, and when they last used it | Person | **Licensing** |
| | Use ranking | Everyone ranked three ways: all use, free use (web Copilot Chat, agents) and Premium use; their Premium share and license | Person | **Licensing** |
| | Better on pay-as-you-go | License holders whose Premium use is at most 0, 5, 10 or 25% of their use, with what Copilot Credits would cost them a month and the saving against the license | Person | **Licensing** |
| | Better with a license | People without a license whose agent and work-grounded chat use costs more in Copilot Credits a month than a license, with a file to license them | Person | **Licensing** |
| | Fading use | License holders whose days of use in the last 30 fell by half or three quarters against the 60 before, but not to none, with their manager | Person | **Licensing** |
| | Copilot on non-human accounts | Copilot licenses on accounts that are not a person, a policy breach: guests, service, shared, test or room accounts, accounts outside the org chart, and the employee types, AD groups and sign-in patterns set in Admin › Reference data; why each was flagged, the files to remove the licenses, and the accounts an administrator confirmed as people, with the reason; license holders by employee type | Person | **Licensing** |
| | Overlapping licenses | People holding the Copilot license with another Copilot-family license (a second Copilot SKU, Copilot Studio, Copilot for Sales or Service), by SKU and by person | Person | **Licensing** |
| | Departures ahead | License holders due to leave in 7, 30 or 90 days, or past their date with the account still enabled, from AD's account expiry and Entra's leave date, with a file to reclaim the past ones | Person | **Licensing** |
| | Swap list | Idle licenses paired with people who want one in the same department, division or anywhere, with files to take each license back and give it on | Person | **Licensing** |
| | Demand for licenses | Unlicensed Copilot Chat users over time and by department; the license type each record reports; the list of them | Department; person | List: **Licensing** |
| | License changes | Licenses assigned and removed: how long each was held, whether it was taken from someone still using it, whether it came back, by department; who made the changes | Scope, department | Who made them: **Licensing** |
| | License pool | Seats bought, assigned and used, per SKU and over time; seats bought and never assigned | Tenant | No |
| | Chargeback | The monthly cost of the licenses each department (or division, company, country, office) holds, its unused share, and the cost per person using Copilot | Attribute | No |
| | Licenses on disabled accounts | Licenses held today by accounts disabled in AD or Entra, or gone from the directory; with the files to reclaim them | Person | **Licensing** |
| Security | Security signals | Jailbreak, prompt injection and blocked resources over time, by weekday and hour, by app, per 1,000 interactions by unit | Scope, unit | No |
| | Security signals by unit | Signals per 1,000 interactions for each unit one level down, over time | Unit | No |
| | Guests and external users | Copilot use by guest accounts, by home organization: people, interactions, licenses held here, signals; the named list for the Security role | Home organization; person | List: **Security** |
| | Flagged interactions | Each flagged interaction: when, who, app, agent, the signal and the Purview record id, with its triage (new, investigating, closed, false positive), owner and note | Interaction | **Security** |
| | Copilot on non-human accounts | The same page as in Licensing, for Security: guests, service, shared, test or room accounts, accounts outside the org chart, and the employee types, AD groups and sign-in patterns set in Admin › Reference data; why each was flagged, the files to remove the licenses, and the accounts an administrator confirmed as people, with the reason; license holders by employee type | Person | **Security** |
| Compliance | Sensitive content | Interactions with labeled content by label and over time; the sites that content lives on; sites Copilot was blocked from; everything it read, by type. Personal OneDrive sites are grouped | Scope, label, site, type | No |
| | Labeled content by unit | The share of each unit's interactions that touched labeled content, over time; resources read by type and label | Unit, type, label | No |
| | Oversharing hotspots | The sites Copilot read from, by how many people reached them through it, with how much of what it read was labeled and its most sensitive label | Site | No (people counts under the minimum group size hidden) |
| | Access to labeled content | Who worked with labeled content, how often, their most sensitive label, how many sites, how often blocked; filter by label | Person | **Compliance** |
| | Person record | One person's directory and license record and every interaction in the period with its record id, downloadable, for a data-subject request or an investigation. Open in privacy mode | Person | **Compliance** |
| | Copilot on non-human accounts | The same page as in Licensing and Security, for Compliance: evidence that Copilot is licensed only to people, and each breach until it is cleared | Person | **Compliance** |
| Agents | Agents in use | Every agent used: people, how many came back, interactions, first use, a trend line; the top six over time | Agent | No |
| | Agent inventory | Every agent seen: where it was built, first and last use, people and interactions in the period, owner and friendly name | Agent | No |
| | Build and publish | Copilot Studio agents created, published, shared, changed and deleted, from Copilot Studio's own audit records; each agent's lifecycle | Scope, agent | Makers: **Agents** |
| | Agent hand-offs | How often one agent calls another, and which agents are called | Scope, agent | No |
| | Agents costing credits | Each agent's use by people without a license, its Copilot Credits a month and per person, and whether to restrict it or license its users | Scope, agent | No |
| | Agent detail | One agent: people and new people, return rate, trend, by department and app; its users | Agent; person | List: **Agents** |
| Enablement | Engagement | Segments (power, regular, occasional, one-off, not started) overall and by department; share of sessions with a follow-up (more than one prompt, each less than an hour after the last, on one day, in any app: the records do not say which prompt answered which) and prompts per session; apps per person | Scope, department | No |
| | Conversation depth | Sessions by prompts and by length; messages per prompt over time | Scope | No |
| | Training effect | For each training session: attendees' active days in the 28 days before and after, against colleagues in the same departments | Session | No (sessions under the minimum group size hidden) |
| | Champions | The most regular and broadest users in each department, as champion candidates | Person | **Enablement** |
| | People by engagement | Named people in a segment: champions to recruit, one-off users to train | Person | **Enablement** |
| Governance | Models and web search | Model mix over time, web grounding, Copilot Chat against in-app, memory updates, calls to another agent, deferred DLP checks, distinct agents in use, agents known by source | Scope; organization-wide for agents known | No |
| | Copilot controls | Memory updates, deferred data-loss checks and web grounding over time; web grounding by unit | Scope, unit | No |
| Forecasting | Forecast | Licenses held, licensed people using them, Copilot Chat demand, interactions and adoption, 13, 26 or 52 weeks ahead, with 95% intervals | Scope, weekly | No |
| | License optimization | Priced recommendations: revoke and reassign, reclaim, upgrade proven Copilot Chat users, train light users, and the right license count for the next quarter; the same by department | Scope, department | No |
| | Budget and renewal | The contracts, what they cost, when they renew, and the seats the use supports at renewal | Tenant | No |
| | Upgrade, train or revoke | The named people behind each recommendation, with the evidence: days and days with Premium use in the last 30, interactions, last use, license age, the license their records report; for upgrades and revokes, an Entra bulk-members file and a PowerShell script to apply them | Person | **Licensing** |
| | Rollout waves | Everyone without a license in the order to license them, split into waves, each with its own file and script | Person | **Licensing** |
| | License action outcomes | Every license export, whether it has been applied, licenses reclaimed and new licenses in use; who is still waiting | Export; person | **Licensing** |
| Regions | Countries and offices | Adoption by country, city and office; people using Copilot by country over time | Country, office | No |
| | Working patterns | Interactions by weekday and hour; the share outside working hours; each app's shape through the day | Scope | No |
| People | Workforce | Adoption by employee type, managers and individual contributors, level in the organization and job title | Attribute | No |
| | Managers and their teams | Team adoption where the manager uses Copilot against where they do not; teams by adoption band | Team | No |
| | Job families | Adoption and use by job family, from the job-title rules in Admin › Classifications and events | Family | No |
| App owners | Apps | Each app's reach, return visits, prompts per person and resources read, with a trend line | App | No |
| | App detail | One app: people over time, what Copilot read in it, departments, agents | App | No |
| Admin | Health, Access, Directory quality, Email, Alerts and digests, Targets and assumptions, Privacy and retention, Reference data, Value names, Classifications and events, Reports shown, Scheduled extracts, Training records, Audit log | See [Running it](#running-it), the sections below and [OPERATIONS.md](OPERATIONS.md) | | **Admin** |

An L2 manager, who reports directly to the CEO, sees the whole organization. The Leadership pages are theirs, and so are the Regions, People, App owners and Governance pages. Whole-organization access doesn't include a role's section or its named lists: those need the role.

The pages whose scope is *Tenant* (License pool, Budget and renewal) are the tenant's in every figure, for a viewer with the Licensing role and their own part of the organization too (2.4.12): they set that viewer's own part's use against the tenant's seats, which made a renewal figure that was neither. The Board pack shows the tenant's seats bought and assigned only to the viewers License pool shows them to, the whole-organization view and the Licensing role, and says they are the whole tenant's when it is printed for a part of the organization.

## Who sees what

**Leaders are never on a revoke or train list (2.4.6).** Levels 1 to N of the organization (Admin › Access › *Never on revoke or train lists*; 5 unless changed) are left out of every list that recommends taking a license away, moving to pay-as-you-go, or training: the pages, their CSVs and removal files, the counts in the digests, Board pack and Budget and renewal, and the data API. Each such list says so above it. Lists of accounts nobody can use (disabled and departed accounts, departures ahead, non-human accounts) keep everyone.

**Who can open the dashboard (2.4.4).** In Admin › Access › *Who can open the dashboard*, an administrator chooses *Everyone in the directory* (the default, as before) or *Levels 1 to N*: level 1 is the top of the organization (`OrgTopUpn`), level 2 reports to them, and so on, to 15. Each choice shows how many people it lets in. With a limit set:

- people at levels 1 to N use the dashboard as before;
- anyone granted a role other than Admin is let in too, whatever their level: Admin › Access is the list of exceptions, for an analyst below the levels;
- everyone else, and anyone the org chart does not place under the top, is refused every page, CSV export, file download, form and data API call, with a page that says which levels it is for, and each refusal is in the audit log. The navigation lists nothing for them;
- an administrator outside the levels keeps the start page and the Admin pages only, so a wrong limit, or a wrong top, can always be put right from the dashboard;
- manager digests go only to managers within the levels.

The level is read from the directory record of the SID-checked sign-in, never from the request. The limit applies from the next page, on every web server (2.4.12: the settings are read once for each page; before, a web server that had not taken the save could take a minute). It is also `Invoke-CopilotMaintenance.ps1 -Task SetDashboardSetting -Name MaxViewerLevel -Value N` (0 for everyone).

**Scope.** Every query is restricted to one part of the organization before anything is counted:

- **The whole organization** for the person set as its top (`OrgTopUpn`, usually the CEO), everyone who reports directly to them, and anyone granted the **Organization** role, wherever they sit.
- **Themselves and everyone under them, at any depth,** for everyone else, from the manager chain in Active Directory (`audit.UserDirectory`). Someone with nobody under them sees their own use.
- **Nothing** for someone who is not in the directory, unless granted Organization.

A viewer can look inside any part of their own scope, one level at a time. The breadcrumb and the "Look inside a team" list offer only what they may open. Each request's `org` value is checked against the org chart on the server, so editing the URL can't widen a scope.

**No part smaller than the minimum group size (2.4.12).** A part of the organization other than the viewer's own is shown only when it holds at least the minimum group size: the manager and everyone under them. The "Look inside a team" list offers only those, and an `org` value naming a smaller part, a person with nobody under them for one, shows the viewer's own part with a note saying why. The data API refuses it (403), and a digest set up for it is not sent. Before 2.4.12 any sign-in in `org` was taken, so every page and the API could be narrowed to one named person.

**Roles** are granted in Admin › Access to a person (UPN or `DOMAIN\user`) or an AD group (`DOMAIN\Group`). One person or group can hold any number of roles: tick them together in one grant, and each shows on their one row in the grants list with its own *Remove*. Group membership is read from the Windows sign-in, so a group grant follows AD with no database change. Power BI has no Windows sign-in to read, so for its *Organization* role the nightly directory load writes the members of every group granted Organization to `audit.GroupMember`: a change to such a group reaches Power BI the next morning.

| Role | Gives |
|---|---|
| Admin | The admin module. **No usage data by itself.** |
| Organization | The whole organization's aggregates, at any level |
| Adoption | The Adoption section; in it, named people who were licensed but have not started |
| Licensing | The Licensing and Forecasting sections; in them, named unused licenses, unlicensed Copilot Chat users, the use ranking, rollout waves and license action outcomes |
| Security | The Security section; in it, named flagged interactions and their triage, and named guests |
| Compliance | The Compliance section; in it, named people who worked with labeled content, personal OneDrive site names, and one person's record |
| Agents | The Agents section; in it, the named users of an agent |
| Enablement | The Enablement section; in it, named people by engagement segment |

**Small groups.** A breakdown never shows a group smaller than the minimum group size (default 5): those groups are folded into "Smaller groups" or left out, so no total describes one person. What is folded or left out reaches the minimum too (2.4.12): when the small groups together are still under it, the smallest groups shown join them, so neither the "Smaller groups" row nor the total minus the rows shown is one person. The rule is the same on every page, digest, extract and API call, and the people who used one agent are such a group: their count is shown only at the minimum or more.

**The audit log.** Every page view, refusal, CSV export and admin change is written to `dash.AuditLog` with who, which page, what scope and which filters, and the client address. What cannot be written there is not served (2.4.12): the write is tried twice, and if it still fails (a full log or disk, a read-only database, the site's account without INSERT on the table) the page, file or API call is answered 503 with a short explanation instead. Before, the failure was logged and the data went out anyway. An admin change made just before such a failure is saved but answered 503 too.

**How a sign-in becomes a person.** IIS authenticates with Negotiate (Kerberos, falling back to NTLM). The site then matches the account's SID, from its Windows token, to `dash.Person.Sid`, which the directory loader copies from AD's `objectSid`, so no directory lookup happens at sign-in. For an account with no SID match, it uses `Insights:IdentityMap` in the settings, then asks Active Directory for the UPN by the SID, then uses the sign-in name if it is already a UPN. See [Servers in one domain, users in another](#servers-in-one-domain-users-in-another).

**Nobody can sign in as someone else.** The viewer is always the account Windows authenticated: nothing in the URL, a cookie or a header changes who they are (the Development-only `?as=` of 2.2.0 is gone). And whichever way the person was found, the SID is checked:

- a person whose directory record carries a SID can be signed in as only by the account with that SID;
- an answer from Active Directory, or a sign-in name that is a UPN, makes the account a person only when the directory records that person's SID and it is the account's (2.4.12). In another domain or forest an account can carry anyone's UPN, and before 2.4.12 such an answer let it in as anyone with no SID on record: a manager outside the directory table, or an administrator granted by UPN who is not in it;
- a mapping in `IdentityMap` is an administrator's word: it is accepted for a person with no SID on record, and refused for one whose record carries a different SID.

Anything refused is logged as a warning, and the viewer is treated as not in the directory. Admin › Directory quality › *No SID recorded* lists the people this leaves out, managers outside the directory table included: load them with their SID, or map their accounts in `IdentityMap`. Grant a role to an account outside the directory as `DOMAIN\name` or through a group, not by UPN.

## Periods

Every report has the same choice: the last **7, 14, 30, 90, 180 or 365 days**, ending on the last complete day with data. The default is 30, which admins can change. Periods of 30 days or less trend by day. Longer ones trend by Saturday-to-Friday week, labeled by the Friday, and the first and last week may be partial.

**A period longer than the data (2.4.12).** A new install holds about 180 days, so a 365-day period reaches back before the first day with data. Figures *a month* (Better with a license, Better on pay-as-you-go, Agents costing credits) are scaled from the days the data covers, not the period's, and Engagement counts its working days from the first day with data. A comparison with the period before (the Scorecard, the Board pack, the Leaderboard and the digests) is made only when the data covers all of that period; otherwise the figure says *no full period before* and the day the data starts, and the Board pack leaves its *Period before* column blank. Before 2.4.12 a 180-day period on 182 days of data was set against the two days before it, "+3997%", and a 365-day period halved everyone's credits a month.

A few views set their own window, and each one says so on the page:

- **Retention by license month:** the last 12 months of license starts.
- **Unused licenses:** today's licenses against an idle time chosen on the page.
- **Agents known:** every agent the log has ever seen.

## Premium use and the license rules

Every interaction made with a license is either **Premium use**, which needed the license, or **free use**, which the free tier gives:

- **Premium:** Copilot inside Word, Excel, PowerPoint, Outlook or Teams; Copilot Chat in work mode, whether or not it read anything; a Microsoft agent; or any chat that read work content (a file in SharePoint or OneDrive, mail, a Teams message, a meeting, a connector item).
- **Free:** web Copilot Chat, Copilot Studio agents (they can be paid for per use without a license), and anything done without a license.

Since 2.4.8 the rule reads what Microsoft writes into each record, its app identity: `Copilot.M365Copilot.Apps` (inside an app), `Copilot.M365Copilot.Bizchat` (Copilot Chat in work mode), `Copilot.M365Copilot.WebChat` (web Copilot Chat) and `Copilot.Studio.*` (an agent). A record with none of these (an older one, or one loaded from a CSV export) is decided by its app's category and by whether it read work content. The web pages a web search cites are listed among a record's resources too, so "it read a resource" alone is never taken for work content. Each interaction keeps the answer in `bi.FactInteraction.UseKind`. A record Copilot itself marked Premium counts as licensed at the time whatever the license dates say.

The rollups keep both per person per day (`dash.UserDay.PremiumUse`), and `bi.Interactions.PremiumUse` carries the same flag to Power BI, so the two always agree. *Train* and *revoke* in Upgrade, train or revoke look at Premium use: a license holder who only used web Copilot Chat or agents in the last 30 days is a revocation candidate, and the row says so ("No licensed use in the records"). *Upgrade* looks at any use, since the people it names have no license. Licensing › Use ranking ranks everyone by all three.

**Better on pay-as-you-go** (2.4.3) lists license holders, licensed since before the period, whose Premium use is a small share of their use. Without a license, web Copilot Chat is free, agents and chat over work content are paid per use in Copilot Credits, and Copilot inside the apps is not available. The page prices each person's Premium and agent interactions, scaled to a month, at the pay-as-you-go cost an interaction in Admin › Access (0.12 by default: 12 credits, a generative answer grounded in tenant data, at 0.01), and sets that against the license cost. An agent grounded only in the web costs nothing, so the estimate errs high on credits and low on the saving. **Better with a license** turns the same price round: people without a license whose metered use (agents, and Copilot Chat that read work content without searching the web) would cost more in credits a month than a license.

"Licensed since" comes from Entra: the date the person's current Copilot assignment began (the earliest of the Copilot SKU's own service plans). The revoke list leaves out anyone licensed for less than 30 days; with no date, it cannot. Until the rollups are rebuilt once with 2.3.0 (the installer does this), the rules count any use, as before, and the page says so.

## Events and hover text

**Every report says who it is for, what it helps with and what to do** (2.4.3), in two short lines under its title: *Helps you* and *Do*, with the section's audience beside them. The longer explanations sit behind **information icons** (ⓘ): beside the title for what the page shows, beside each panel for how its figures are worked out, and beside a KPI for what it counts. Hover over an icon, focus it with the keyboard or tap it for the text; Escape or a second tap closes it. A KPI's short note with a figure in it stays on the card. Printed pages, the board pack included, show every icon's text in place. Admin pages keep their instructions on the page.

**Events** (Admin › Classifications and events) show on every trend as a numbered badge above the chart with a light dashed line through it: a rollout wave, a training push, an outage, a policy change. Their days and names are in a key under the chart, so nothing is written over the data (2.4.5). Events in the same week or day share a badge; hover a badge for each event and its kind.

**Every label is shown whole.** Chart margins and label columns size themselves to the longest label, and long names wrap onto more lines rather than being cut short. Hover over any mark for its exact figures.

## More than one tenant

A group with several Microsoft 365 tenants keeps each in its own database (loaded by its own app registration) and runs one dashboard over them: `Insights:Tenants` in the settings lists each tenant's name and connection string (the installer's `-Tenant` writes it). The header then has a tenant switcher, remembered for the session, and Leadership › Group overview puts the tenants side by side. Who someone is, and what they may see, is worked out again in each tenant's database from their Windows sign-in: a person sees a tenant only where its directory has them or they are granted a role there. A form posts to the tenant its page showed (2.4.12): before, it posted to whichever tenant the switcher named when it was sent, so a change made on a page left open from another tenant landed in the wrong one; a form that names no tenant changes nothing. The background checks and digests run in every tenant; **Run the checks for every tenant** runs them only where the person is an administrator (2.4.12). [OPERATIONS.md](OPERATIONS.md#more-than-one-tenant) has the steps.

## Forecasts and recommendations

**Forecast.** Each weekly series (the last 52 complete Saturday-to-Friday weeks in the viewer's scope) is projected with damped-trend exponential smoothing: Holt's method with the trend damped by 0.9 a week, its two smoothing parameters chosen by a grid search on the one-step error. Damping keeps a recent climb from being projected upward for ever, as rollouts level off. The shaded band is a 95% interval from the fitted error, widening with the horizon. It projects history: a planned rollout or reclaim will move it. A forecast needs 8 weeks of history.

**Recommendations**, from the last 30 days and the price per license in Admin › Access:

| Recommendation | Who |
|---|---|
| Upgrade to Premium | No Premium license, and used the free Copilot Chat on 6 or more of the last 30 days |
| Train, then review | A Premium license, used on only 1 or 2 of the last 30 days |
| Revoke | A Premium license held at least 30 days, with no use at all in the last 30 (or never) |
| Revoke and reassign | As many revoke candidates as there are upgrade candidates: moving a license costs nothing |
| Right size for the next quarter | Licenses for the people the forecast expects to use one each week in 13 weeks, plus 10%, plus today's upgrade candidates, against the licenses held |

The named lists are on **Forecasting › Upgrade, train or revoke**, for the Licensing role; the recommendation cards link to them for people who hold it.

## Long lists and exports

Every list of people is paged on the server: 50 rows per page, a search box (name, sign-in, department), and a fixed set of sorts. Only one page of rows ever leaves the database, so a list of 40,000 people costs the same to show as a list of 50.

**Every table sorts by its column headers (2.4.5).** Click a header to sort by it, and again to reverse; the arrow beside it shows the order, and screen readers announce it. Numbers, percentages, money and dates sort by value, not text: numbers and dates open largest or latest first, text A to Z. A paged list of people is sorted in the database, so the order covers every page and the CSV export, not just the 50 rows on screen; the sort choice beside its search box says when a column, or a reversed order, is in force. Columns of buttons or small trend charts don't sort, nor do tables whose order is their meaning (a funnel's steps, pipeline steps, rules in priority order, levels, rollout waves), and a total or *Smaller groups* row stays at the bottom. **Download CSV** exports every row matching the current search and sort, UTF-8 with a BOM for Excel. Text cells that would start a spreadsheet formula are prefixed with `'`; numbers are written as numbers, negative ones too, and a cell the page shows as formatted text (a signal, a linked name, the days to a renewal) exports its text (2.4.12: such cells were empty, and negative figures were text a spreadsheet did not add up). A column of buttons exports nothing. Each export is audit-logged with its row count.

The pages read two rollups rather than the raw facts:

- **`dash.UserDay`:** one row per person per day with any use.
- **`dash.ResourceDay`:** labels and sites per person per day.

**Report speed (2.4.7).** The data behind the reports changes at the nightly refresh, when a pipeline run ends, when the licenses or the directory are read, and when an administrator changes a setting or reference data; between those, a query gives the same rows every time. So a report page's query results are kept in memory until the data changes, and after each change the site builds every report once in the background for the whole organization, at every period (about 5 minutes at 40 million interactions). Since 2.4.12 a change removes what was kept before it (it used to stay in memory, unread, for 12 hours), at most `Insights:QueryCacheEntries` (20,000) results are kept, the least recently used going first, and the pages are built again when the last build's results expire, not only at the next change; a pipeline run counts when it ends, not when it starts, and the license read counts every table it writes, not only the first. The top of the organization, their direct reports and holders of Organization then get each page from memory; a manager's own part of the organization is kept from the first time someone opens it. A result is kept under the tenant, the query's text and every parameter, the scope among them, so it only ever answers a request that would have run the very same query; who the viewer is and what they may see are still settled from the database on every request, on Leadership › Group overview too (2.4.12). Admin pages (but for the labels Admin › Reference data has seen), CSV and file exports, the data API and every form always read the database. Each web server keeps its own results and builds its own pages: see [Several web servers](#several-web-servers). A list edited through the day (flag triage, exported license actions, confirmed accounts) has a version of its own, read from the database for every page that shows it (2.4.11): a page that reads one is kept until that list changes, and a change, made on this web server or another, shows on the very next page. Admin › Health › *Report speed* shows when the pages were last built. `Insights:QueryCacheMinutes` (720) in the site's settings sets how long a result may be kept at most, and 0 turns all of this off; `Insights:WarmPages` (true) turns only the background build off. Measured on 70,000 people and 40 million interactions as the head of the organization, every page loads in under 2 seconds for a year; on 210,000 people and 120 million interactions (2.4.11), in under 1.5 seconds once built, and the build takes 14 minutes ([the figures](DEPLOYMENT.md#120-million-interactions-the-2411-test)).

Both are rebuilt incrementally with the rest of the pipeline. On the demo (2,317 people, 180,000 interactions), pages take 50–300 ms and the cohort page 0.6 s.

## Email, alerts and digests

The site sends its own email, from a background task that runs every five minutes with the site (the installer starts the site with IIS, so it runs with nobody signed in). With several web servers, one sends at a time: they share a lock in SQL Server. Each alert, digest and manager digest is also marked as sent before it goes (2.4.12), so a database that can be read but not written sends nothing rather than the same message every five minutes, and two servers never send one twice. Everything is set in **Admin › Email** and **Admin › Alerts and digests** and stored in the database; nothing is in a settings file.

**How it sends** (Admin › Email):

| Mode | Server | Sign-in |
|---|---|---|
| Exchange Online, Password | `smtp.office365.com`, port 587, STARTTLS | SMTP AUTH with the sending mailbox's password. The mailbox needs Authenticated SMTP allowed. Microsoft turns SMTP AUTH with passwords off by default for existing tenants at the end of December 2026 (administrators can turn it back on until a removal date announced in 2027); use OAuth for anything long-lived. |
| Exchange Online, OAuth | the same | An app registration with the Office 365 Exchange Online application permission `SMTP.SendAsApp`, registered in Exchange Online (`New-ServicePrincipal`) and given Send As on the mailbox. The client secret is stored encrypted. This is an Exchange permission, not a Microsoft Graph one; use a separate app registration to keep the pipeline's app at its three Graph permissions. |
| SMTP server | any host and **any port** | Optional user name and password. **StartTls** upgrades a plain connection (25, 587, 2525); **Implicit** is TLS from the first byte (usually 465); **Auto** decides by port; **None** sends in the clear. The server's certificate is checked unless *Accept any certificate* is ticked (a lab relay only). |

Passwords and client secrets are stored in `dash.Secret`, encrypted by SQL Server (a symmetric key protected by a certificate and the database master key). The site's account reads them only through `dash.usp_GetSecret`; it cannot select the table. They are never shown again: leave the field empty to keep what is stored. A stored password is sent only to the server, port, security and user name it was entered with (2.4.12): change any of them and it is not used until it is typed again, so a changed server never receives the password meant for another. Restored to another SQL Server instance, they cannot be decrypted: run `Install-CopilotAudit.ps1` again, which makes the keys again and removes the secrets it cannot read (2.4.12; before, the keys that could not be opened were kept and nothing could be stored), then enter them again. **Send a test** sends one message and shows the server's answer; everything sent, and anything that could not be, is listed with the reason.

**Alerts** (Admin › Alerts and digests), each to its own recipients, posted to the webhook if one is set, and each written to the Windows Application log under the source *Copilot Insights* (event 1001 pipeline, 1002 stale data, 1003 certificate, 2001 security, 3001 drops and spikes, 4001 license pool, 4002 licenses on disabled accounts, 5001 new agents), for a SIEM to collect. The entry is written on the web server that ran the check, so with several web servers collect from each:

| Alert | When |
|---|---|
| Pipeline failed | A run of `Invoke-CopilotPipeline.ps1` failed: the step and its last log lines. Also a run still marked as running after 12 hours. |
| Stale data | The dashboard's data is older than *FreshnessHours* (36). Once a day while it lasts. Every page also shows a banner. |
| App certificate | The certificate the pipeline signs in to Graph with expires within *CertificateWarnDays* (30), then again at 14 and 3 days, and when it has expired. |
| Security signals | After each refresh, for the last complete day: jailbreak attempts, prompt injections, blocked resources, and interactions touching content at or above a label order, when a day reaches its threshold or is the set number of times its 28-day average. |
| Drops and spikes | A department's day at least 50% below, or 200% above, its average for that weekday over the four weeks before. Departments smaller than the minimum group size are not judged. |
| License pool | 95% of the Copilot seats bought are assigned. Once a week while it lasts. |
| Licenses on disabled accounts | More of them than at the last check. |
| New agents | An agent used for the first time. |

An alert fires once per occurrence (`dash.AlertState` remembers what it last saw, and the check moves it on before the alert is sent). An alert whose email the server refused or did not answer, with no webhook to take it either, is emailed again an hour later, three tries in all (2.4.12; before, it was marked raised and never emailed); *Where the checks stand* shows one waiting. The very first check notes where things stand rather than announcing every existing agent and leaver. **Run the checks now** runs a round at once and says what it did.

**Digests** are scheduled emails: *Leadership*, *Adoption*, *Licensing*, *Security* or *Value*, daily, weekly or monthly at an hour in the reporting time zone, for the whole organization or one manager's, over a period of 7 to 365 days. They carry aggregates only, never a named person, so they can go to anyone the scope suits. **Preview** shows one as it would arrive; **Send now** sends one today. Both need the role of the section the digest shows (Adoption, Licensing or Security), as its pages do, and a preview is in the audit log (2.4.12). On the days the clock changes, a digest set in the hour the clock skips goes when it reaches the other side, and one in the hour it repeats goes the first time round (2.4.12; before, the first stopped every check for the day, and the second was sent about 13 times).

**Manager digests** go to every manager with at least a set number of people under them, weekly or monthly: the Leadership digest for their own organization, to the email address AD holds for them. An administrator stops one manager's in Admin › Alerts and digests › *Managers who do not get the digest* (opt-outs made before 2.4.4 are kept). When the dashboard is limited to levels, only managers within them get it. They are not sent in privacy mode, since a unit is named after its manager.

A manager digest that fails is tried again an hour later, three times in all (`dash.DigestSend` records each person's send for the period), and so is a scheduled digest set up in Admin. One cut off by the site stopping before its email went is given back and goes when the site starts again (2.4.12).

**The webhook**: every alert is also posted to one https address, stored encrypted like the email password: to Teams as an adaptive card (a Workflows "post to a channel when a webhook request is received" address, or an incoming webhook), or as JSON (`source`, `kind`, `subject`, `lines`, `link`, `atUtc`) for ServiceNow, Splunk or anything else. Addresses on this server or link-local ones are refused. A Teams card shows the alert as plain text (2.4.12): before, it read the lines as Markdown, so an agent named `[Approve](https://…)` became a link in the alert channel.

**Scheduled extracts** (Admin › Scheduled extracts) write aggregate CSV files to a folder or share, daily, weekly or monthly: department by week (groups under the minimum size hidden), daily totals, apps by week, the license pool, agents by week (agents with fewer users than the minimum group size left out). No extract names a person. With several web servers, write to a share: a round runs on whichever server holds the lock, so a local folder would get only the files of the days that server ran. An extract to a local folder fails, and says why, once another web server has run the checks in the last day, and the result of one written to a local folder names the server (2.4.12).

## Targets, value, training and champions

**Targets** (Admin › Targets and assumptions): for the whole organization or one manager's, *Adoption* (licensed people using it, over people licensed), *Share of people active*, or *People active*, from a start date to a due date. Adoption › Targets shows each against its baseline (the 30 days before it started), where it is now (the last 30 days) and where a straight line to the target says it should be: met, on track, behind or missed. A report shows the targets for the part of the organization it covers (2.4.12): every target for the whole organization, otherwise those for a manager inside that part, never the whole organization's. Adoption › Targets, the Board pack, the Adoption digest and `/api/v1/targets` share the rule; before 2.4.12 only the Targets page kept to it. A target for a part smaller than the minimum group size shows no figures, except to that manager.

**Value**: the minutes one interaction saves, by default and per app, and what an hour is worth. They are your organization's own estimates (from a survey or a pilot); Leadership › Value and time saved, the Value digest and the API follow them. The license cost set against the value, on Value and time saved and the Board pack, is the licenses held on each day of the period at the monthly price (2.4.12): it was today's licenses times every day of the period, so a rollout during the period, or a period reaching back before the licenses, was charged in full.

**Training records** (Admin › Training records): a CSV of UPN, session and date (yyyy-MM-dd) from your learning system. Enablement › Training effect compares attendees' active days in the 28 days before and after each session with colleagues in the same departments who did not attend, four weeks after the session.

**Champions**: Enablement › Champions suggests the most regular and broadest users in each department (days active, then apps used, then back-and-forth conversations), as many per department as set. It is a named list, for the Enablement role.

**License exports**: Upgrade, train or revoke, Licenses on disabled accounts and Rollout waves offer the list as an **Entra bulk add or remove members file** (Groups › Bulk operations) and as a **PowerShell script** for a license administrator to review and run with their own rights. With the Object id of the group that assigns Copilot licenses set in Admin › Targets and assumptions, the script adds and removes its members; without it, it assigns or removes the SKU itself. The script confirms each change and takes `-WhatIf`. The dashboard changes nothing in Entra. Each export is recorded (`dash.LicenseAction`) so Forecasting › License action outcomes can show whether it was applied and what followed. The files come from buttons, not links (2.4.12): an export records who it names, so it is a form posted with the page's anti-forgery token, and a link on another page or in an email can no longer make one in the viewer's name. Each covers the part of the organization the page shows. An export is recorded whole or not made: if the record cannot be written, the download fails and can be tried again, where before it was given out recorded in part or not at all. A person already recorded for the same change that day is not recorded again.

**Contracts** (entered by an administrator on Forecasting › Budget and renewal): each agreement's seats, price and renewal date. The page compares them with the seats the use supports: seats assigned today, less licenses to revoke and on disabled accounts, plus people ready to upgrade.

**Time zone** (2.4.10, Admin › Access › Settings): the zone every time is shown in and schedules run by; by default the server's, as the data's days are cut. See [OPERATIONS.md](OPERATIONS.md).

**Reports shown** (2.4.9, Admin › Reports shown): an administrator turns any report off, and it is gone for everyone, administrators too: from the navigation, the start page, and at its address. The page lists the reports by section as the start page does, each with a box to tick. See [OPERATIONS.md](OPERATIONS.md).

**Classifications** (Admin › Classifications and events): job-title patterns to job families (People › Job families), and app, web-search and agent patterns to use cases (Leadership › Use cases). The first matching rule by priority wins; both apply at once, to past data too, and to Power BI through `bi.JobFamilies` and `bi.UseCases`.

## Privacy mode and retention

**Privacy mode** (Admin › Privacy and retention), for organizations that must not report on named people, such as under a works council agreement:

- every page, export, digest and API call that names people is refused, and hidden from the navigation, and the named lists on other pages are left out (Demand for licenses, Agent detail, Who made the changes, the people who worked with labeled content). Compliance › Person record stays open to the Compliance role, for data-subject requests;
- a part of the organization is named after its manager, so in privacy mode (2.4.12) units go by their manager's department, numbered where two share one, and are not links; nobody can open a manager's part by name (the "Look inside a team" list is empty, and an `org` value shows the viewer's own part); a digest set up for a manager's part is not sent, and a target for one is shown by its department. A personal OneDrive's address names its owner, so it is shown as *OneDrive (personal site)*;
- if the setting cannot be read, privacy mode counts as on until it can (2.4.12): before, a failed read turned it off for a minute;
- it applies from the next page, export or API call on every web server, as do the minimum group size and Reports shown (2.4.12): each request reads them from the database. Before, a web server other than the one that took the save went on for up to a minute. The background work (digests, alerts, scheduled extracts) reads them at most a minute old;
- Power BI's `bi.Users` shows a salted hash (`P-…`) instead of every name, sign-in, manager and org level, the same for the same person everywhere, so counts and org slicing still work;
- `bi.OrgSecurity` is empty (its rows would pair real sign-ins with the pseudonyms), and OneDrive site names and the administrators in `bi.LicenseChanges` are hidden;
- `bi.DepartmentWeek` gives weekly department figures with any group under the minimum size hidden, for a model that must not show individuals.

**Retention**, applied each night after a successful run and on demand: the dashboard's audit log (730 days by default, and never less than 365, 2.4.12: it is how administrators are held to account; a change is recorded with its old and new values), the notification log (365), and the stored JSON of old records (off by default; at least 200 days, since past Purview's 180 days it cannot be fetched again; the reports keep working without it).

## Value names

The audit log records many things as codes: sensitivity label ids, the tenant id, app and agent ids, Fabric capacities, license types, model names. The flatten keeps a catalog of every distinct value of 23 such columns (`audit.ValueCatalog`); a column that passes 10,000 distinct values stops collecting and is marked capped, since a list that long is not one to name by hand.

**Admin › Value names** lists the columns and their values, most seen or unnamed first, with a name and a note for each. **Download** writes a column's values and names to a CSV to fill in; **Upload** reads it back (column id, value, name, note; an empty name removes one). Names show on the dashboard's pages where those codes appear (apps, models, resource types, license types, agents called) and in Power BI through `bi.ValueNames`. Label names are shared with Admin › Reference data, where their order is set. Agents are named in Admin › Reference data, and that name is the one pages, digests and alerts show; a name for an agent id here is for Power BI only (2.4.12: since 2.4.11 a Copilot Studio or Microsoft agent is known by its app identity, which no agent id matches, so the link named nothing). A file saved again in Excel keeps a name with a comma or semicolon in it whole (2.4.12), and a line with more cells than it should have is refused rather than cut. The same from PowerShell:

```powershell
.\tools\Invoke-CopilotMaintenance.ps1 -SqlServer SQL01 -Task ListValues -ColumnId 16 -Path models.csv
.\tools\Invoke-CopilotMaintenance.ps1 -SqlServer SQL01 -Task ImportValueNames -Path models.csv
.\tools\Invoke-CopilotMaintenance.ps1 -SqlServer SQL01 -Task RebuildValueCatalog   # every value again (the installer does it once on upgrade)
```

## The data API

`/api/v1` answers JSON for other tools (a service desk, an HR system, a SIEM), under the same Windows sign-in, scope and roles as the pages. `?d=` is the period and `?org=` a manager's UPN, as on the pages. `/api/v1` lists the endpoints:

| Endpoint | Gives | Needs |
|---|---|---|
| `/api/v1/usage` | totals and the trend; its jailbreak, injection and blocked counts for the **Security** role only, its labeled content for **Compliance** | data to see |
| `/api/v1/units` | each unit one level down, small ones combined; signals for **Security**, labeled content for **Compliance** | data to see |
| `/api/v1/security` | security signals by day or week | **Security** |
| `/api/v1/license-pool` | seats bought and assigned per SKU | **Licensing** |
| `/api/v1/value` | hours saved and their worth, by app and department, small departments combined | data to see |
| `/api/v1/targets` | each target for the caller's part of the organization and where it stands | **Adoption** |
| `/api/v1/license-actions?kind=upgrade\|train\|revoke` | the people behind a recommendation | **Licensing** |
| `/api/v1/leavers` | licenses on disabled or departed accounts | **Licensing** |
| `/api/v1/health` | pipeline runs and freshness | **Admin** |

Every call is in the audit log (kind *Api*), the list of endpoints at `/api/v1` too (2.4.12: it answered with the API off, to anyone outside the dashboard's levels, and was not logged); refusals answer 403 with the reason. Named endpoints are refused in privacy mode, and so is an `org` value naming a manager's part; one smaller than the minimum group size is refused always (2.4.12). Admin › Privacy and retention can turn the API off (it then answers 404). A script calls it as its own Windows account, for example `Invoke-RestMethod https://insights.contoso.com/api/v1/usage?d=30 -UseDefaultCredentials`.

## Installing it on IIS

### Prerequisites

- Windows Server 2022 or later, joined to the users' domain or to a domain that trusts it (see [Servers in one domain, users in another](#servers-in-one-domain-users-in-another)).
- The ASP.NET Core 10 **Hosting Bundle**, installed beforehand or passed to the installer with `-HostingBundlePath`. It is the download labeled *Hosting Bundle* on the .NET 10 page; the .NET and ASP.NET Core Runtime installers lack the IIS module. It is needed whichever `-PackagePath` you use. If it was installed before IIS, run it again and choose Repair.
- The site to install: the release's pre-built `copilot-insights-v<version>.zip`, matching the scripts' version, or the `dashboard\CopilotInsights` source folder on a server with the .NET 10 SDK. Without the SDK, a source folder fails with *dotnet.exe : The command could not be loaded*.
- The Copilot database with the `bi` schema filled (`Install-CopilotAudit.ps1`, deployment steps 4-11). The installer below adds the dashboard's own `dash` schema to it and builds its rollups when run with `-GrantDatabaseAccess`; nothing needs running by hand. The top of the organization is found in the org chart (the one person with no manager who has at least half the directory under them); `-OrgTopUpn` or Admin › Access names someone else.
- An account for the app pool. A **group managed service account** is best: it needs no password and can have its own SPN. An account with a password works too, with `-AppPoolCredential`.
- A certificate for the site's host name, in LocalMachine\My, or a start on HTTP (see [Host name, DNS and certificate](#host-name-dns-and-certificate)).
- `-TrustServerCertificate` when SQL Server presents its own self-signed certificate. The site connects encrypted, and without it every page says *Something went wrong*. From 2.4.1 the installer tries the connection and warns.

### Run the installer

In an elevated **Windows PowerShell 5.1** (`powershell.exe`, not PowerShell 7: the IIS provider it uses exists only there):

```powershell
.\dashboard\Install-CopilotInsights.ps1 -PackagePath .\CopilotInsights `
    -SqlServer SQL01 -Database CopilotAudit `
    -AdminPrincipal 'CONTOSO\Copilot-Insights-Admins' `
    -AppPoolIdentity 'CONTOSO\gmsa-insights$' `
    -HostName insights.contoso.com -CertificateThumbprint 3F2A... `
    -GrantDatabaseAccess
```

| Parameter | What it is |
|---|---|
| `-PackagePath` | The published app, as a folder or a zip (the release's `copilot-insights-v<version>.zip`; don't unzip it). It can also be the `dashboard\CopilotInsights` source folder, which the installer publishes itself when the .NET 10 SDK is installed. |
| `-SqlServer`, `-Database` | Where the Copilot database is. Add `-TrustServerCertificate` for an instance with a self-signed certificate. |
| `-AdminPrincipal` | The **people** who administer the site from the first sign-in: `DOMAIN\Group` (best), `DOMAIN\user`, or the UPN of someone the directory records with their SID (2.4.12: an account outside the directory is no one by its UPN), from the domain people sign in with. They are written to `appsettings.Production.json`, not SQL Server. Keep it short and grant everyone else in Admin › Access. A service account doesn't belong here: nobody should sign in to a browser as one. |
| `-AppPoolIdentity` | The **account the site runs as**, and connects to SQL Server as. A gMSA (`DOMAIN\name$`), or an account with a password given as a credential: `-AppPoolIdentity 'CORP\svc-insights' -AppPoolCredential $cred`, where `$cred = Get-Credential -UserName 'CORP\svc-insights'`. The name is text; the credential is the object. Left out, the site runs as the app pool's own identity, which reaches a remote SQL Server as the web server's computer account. |
| `-HostName`, `-CertificateThumbprint` | The address people browse to, and the thumbprint of a certificate for it. See [Host name, DNS and certificate](#host-name-dns-and-certificate). |
| `-GrantDatabaseAccess` | Installs the `dash` schema and grants the app-pool account its rights, which needs rights to create logins. Without it, the grants are written to a file for a DBA, and the `dash` schema comes from `Install-CopilotAudit.ps1 -Dashboard`. |
| `-HostingBundlePath` | The Hosting Bundle installer, run when the bundle is missing. |

The installer is idempotent. It:

1. Installs the IIS role services: Windows authentication, static content, logging, request filtering and the management console.
2. Checks for the Hosting Bundle, and installs it from `-HostingBundlePath` when given.
3. Copies the site to `C:\inetpub\CopilotInsights`, keeping `appsettings.Production.json`. It takes the site offline with `app_offline.htm` while the files change.
4. Writes `appsettings.Production.json`: the connection string, and the bootstrap administrators under `Insights:BootstrapAdmins`.
5. Creates the app pool (no managed code, always running) and the site, with an HTTPS binding for the host name (SNI, so it can share port 443 with other sites), or an HTTP binding without `-CertificateThumbprint`. Run again with a different host name or protocol, it adds that binding and leaves the others; remove one you no longer want with `Remove-WebBinding`.
6. Turns Windows authentication on and anonymous authentication off.
7. Writes the database grants for the app-pool account to `Grant-CopilotInsightsAccess.sql`, and runs them with `-GrantDatabaseAccess`, which needs rights to create logins. An account that owns the database is `dbo` in it and needs no grants; the script says so and moves on. One a DBA already added under another user name is granted under that name. The grants are:
   - SELECT on the `bi` and `dash` schemas;
   - changes to the `dash` settings, roles and reference tables;
   - INSERT on the audit log;
   - EXECUTE on the refresh procedure;
   - SELECT on `audit.UserDirectory`, `audit.PipelineSetting` (privacy mode is read from it: without it the dashboard treats privacy mode as on) and the pipeline's run logs;
   - changes to `audit.SessionAppCategory`.

For Kerberos on a host name, register the SPN on the app-pool account: `setspn -S HTTP/insights.contoso.com CONTOSO\gmsa-insights$`. Add the host name to the browsers' Local intranet zone so they sign in silently.

Host it as a site of its own, not as an application under another site: its links are rooted at `/`.

### Host name, DNS and certificate

`-HostName` is the address people type, and IIS answers it only for that name. Two things have to agree with it:

- **DNS.** A record that points the name at the web server. Without one, browsers can't find the site.
- **The certificate.** Issued for that exact name, installed with its private key in *Local Computer › Personal* (LocalMachine\My) on the web server itself. A certificate for `contoso.com` doesn't cover `insights.contoso.com`, and the job server's `copilot-audit` certificate is for signing in to Graph, not for a website. Copy the thumbprint from PowerShell rather than the certificate dialog, which can paste an invisible character with it:

  ```powershell
  Get-ChildItem Cert:\LocalMachine\My | Select-Object Thumbprint, @{n='Names';e={$_.DnsNameList -join ', '}}, NotAfter, HasPrivateKey
  ```

Use a name of its own, such as `insights.contoso.com`, not the organization's main domain. Your DNS and certificate teams usually provide both.

**Starting without them.** The server's own full name already resolves, and many organizations enroll each server a certificate for it:

```powershell
$fqdn = [System.Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName; $fqdn
Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.DnsNameList -contains $fqdn } | Select-Object Thumbprint, NotAfter, HasPrivateKey
```

Pass that name as `-HostName`, with the certificate's thumbprint if there is one, or without `-CertificateThumbprint` to answer on HTTP. `-HostName localhost` without a certificate is for a test on the server itself: nobody else can reach it. Move to a proper name later by running the installer again with it; the settings are kept.

With the server's own name, sign-in works by NTLM, or by Kerberos with the computer account's SPN, which is already registered. The installer sets IIS to use the app-pool account's credentials for Kerberos whenever `-AppPoolIdentity` is given, which suits a dedicated name with its SPN on that account. For the server's name with Kerberos, turn it off afterwards (and again after each run of the installer):

```powershell
Set-WebConfigurationProperty -PSPath 'IIS:\' -Location CopilotInsights `
    -Filter 'system.webServer/security/authentication/windowsAuthentication' -Name useAppPoolCredentials -Value $false
```

### Another site on port 443

IIS picks the most specific binding: IP address and host name, then IP address alone, then host name alone, then neither. The installer's binding is the host name on every address (`*:443:insights.contoso.com`). If another site on the server is bound to the server's IP address with no host name (`10.0.0.5:443:`), it wins, and people browsing to the dashboard reach that site instead. Bind the dashboard to the same address with its host name, which outranks it:

```powershell
New-WebBinding -Name CopilotInsights -Protocol https -IPAddress 10.0.0.5 -Port 443 -HostHeader insights.contoso.com -SslFlags 1
(Get-WebBinding -Name CopilotInsights -Protocol https -IPAddress 10.0.0.5 -HostHeader insights.contoso.com).AddSslCertificate('<thumbprint>', 'My')
```

`netsh http show sslcert` lists every certificate registered for HTTPS on the server. An entry whose *Application ID* isn't IIS's `{4dc3e181-e14b-4a21-b022-59fc669b0914}` belongs to another program; leave it alone.

### NTLM only

To sign in without Kerberos, remove the Negotiate provider and keep NTLM. No SPN is needed then, and `useAppPoolCredentials` no longer matters:

```powershell
$f = 'system.webServer/security/authentication/windowsAuthentication/providers'
Remove-WebConfigurationProperty -PSPath 'IIS:\' -Location CopilotInsights -Filter $f -Name '.' -AtElement @{ value = 'Negotiate' }
Get-WebConfigurationProperty -PSPath 'IIS:\' -Location CopilotInsights -Filter $f -Name Collection | Select-Object value   # NTLM
iisreset
```

The installer doesn't change the providers, so this survives running it again. Browsers still sign in silently only to sites in the Local intranet zone. Check first that your organization doesn't restrict NTLM on servers.

### Servers in one domain, users in another

This is the usual layout: the web, SQL and job servers sit in a resource domain, and people sign in with accounts from a users domain.

**Both trust directions are used.**

| Trust | What needs it |
|---|---|
| The users domain trusts the resource domain | The resource-side accounts reading the users domain. `tools/Export-UsersWithHierarchy-ToSql.ps1 -TargetDomain <users domain>` on the job server binds to the users domain's AD as the job's account. The site's directory fallback does the same. |
| The resource domain trusts the users domain | People with users-domain accounts signing in to the site, by Kerberos or NTLM. Also SQL logins for users-domain accounts or groups, such as a reporting group or an administrator who runs the installers. |

With only the first trust, the loader works but nobody from the users domain can sign in. Use a two-way trust, or a one-way trust in each direction. If a trust uses selective authentication, give the users (or a group of them) *Allowed to authenticate* on the web servers' computer objects. Only the web servers need it: the site reaches SQL Server as its own resource-domain account.

**Matching a sign-in to a person.** The loader copies each user's `objectSid` into `audit.UserDirectory.Sid`, and the refresh copies it into `dash.Person.Sid`. The site matches the signed-in account's SID to it, so the users domain is not contacted at sign-in. It works even when the users domain's NetBIOS name doesn't resolve from the resource domain. Accounts without a match fall back to `Insights:IdentityMap`, then to a directory lookup, which names the account a person only when the directory records that person's SID and it is the account's (2.4.12).

**Grants.** Name the users domain's groups: `USERS\Copilot-Insights-Admins` for `-AdminPrincipal`, and in Admin › Access or `-Task GrantRole`. Global and universal groups from the users domain are in each user's sign-in token. A domain-local group in the resource domain that holds users-domain members works too.

**Kerberos.** Register the SPN on the app-pool account in the resource domain: `setspn -S HTTP/insights.resource.corp RESOURCE\gmsa-insights$`. The users domain's domain controllers must be able to find that SPN:

- **Same forest:** found through the global catalog. Nothing more to do.
- **Separate forests (a forest trust):** the host name's DNS suffix must be routed to the resource forest. Use a host name in the resource forest's namespace, or add the suffix to the trust's name suffix routing.

If the SPN can't be found, the browser falls back to NTLM. NTLM still works, but is slower and doesn't delegate. Either way, add the host name to the browsers' Local intranet zone.

### Several web servers

One web server needs none of this. With two or more behind a load balancer, all reading the same database:

- **One set of form keys (2.4.12).** Every form carries an anti-forgery token, and the message after a save travels in a cookie; both are protected with keys that, by default, each server keeps in its own app pool's profile. A form drawn by one server and posted to another is then refused (HTTP 400), and the message after a save is lost when the next page comes from the other. Give the servers one set of keys: a share every server's app pool account can change (Modify), named in `appsettings.Production.json` on each as `"Insights": { "DataProtectionKeysPath": "\\\\fs01\\insights-keys" }`. The keys there are protected for the account the site runs as (DPAPI-NG), so every server must run as the same domain account or group managed service account (`-AppPoolIdentity`); or name a certificate instead, `"DataProtectionCertificate": "<thumbprint>"`, installed with its private key in *Local Computer › Personal* on every server and readable by the app pool account. Recycle the app pools; the log says *Form keys shared by the web servers* at start-up. If the keys cannot be protected (a local account, or the certificate missing or its private key unreadable), the site does not start, and its log says why, rather than refusing every form. A form open in a browser at that moment is refused once. Without a shared folder, use session affinity (sticky sessions) on the load balancer instead.
- **The same settings file on each.** `appsettings.Production.json` is per server: a change to `BootstrapAdmins`, `IdentityMap` or the connection string is made on every server, and each app pool recycled.
- **Settings apply everywhere at once.** Privacy mode, the minimum group size, Reports shown and the level limit are read from the database for each page (2.4.12), so a change saved on one server applies to the next page on all of them.
- **Each server keeps its own report cache and builds its own pages.** After each data change, and at midnight UTC, every server builds every report in the background, so SQL Server does that work once per server (14 minutes each at 120 million interactions, side by side); a manager's part of the organization is kept only on the server that first built it, so without session affinity their next visit to another server is slow again. Use session affinity to keep a person on one server, and if the build load matters, set `"WarmPages": false` on all but one or two servers: the others then build each page when first asked. Sharing one cache across servers (a distributed cache) is not built.
- **Email, alerts and digests** run on one server at a time: they share a lock in SQL Server. Nothing to set.

## Enrolling the first administrator

The site's configuration names its first administrators. The database's grants don't, so there is always a way in:

1. Run the installer with `-AdminPrincipal`, naming an AD group (recommended) or a person as `DOMAIN\user` or a UPN. It writes them to `Insights:BootstrapAdmins` in `appsettings.Production.json`.
2. A member signs in and opens **Admin › Access**. They set the top of the organization, then grant roles, including Admin for the people who will run it day to day.
3. Bootstrap administrators aren't listed in Admin › Access and can't be removed there. To change them, edit the settings file, or run the installer again with a new `-AdminPrincipal`, then recycle the app pool. With several web servers, each has its own settings file: change it on every one, or a bootstrap administrator removed on one keeps Admin on the others.

If the settings file is lost and nobody holds Admin, run the installer again with `-AdminPrincipal`, or grant it from the job server:

```powershell
.\tools\Invoke-CopilotMaintenance.ps1 -SqlServer SQL01 -Task GrantRole -Principal 'CONTOSO\Copilot-Insights-Admins' -Role Admin -Note recovery
```

Admin shows no usage data by itself. An administrator who should also see the reports needs their level or the Organization role, like anyone else.

## Running it

**Refresh.** Nothing extra to schedule. `Invoke-CopilotAuditBackfill.ps1 -Incremental` and the CSV importer run `dash.usp_RefreshDashboard` last, after `bi.usp_RefreshBi`, from the same day. Each refresh rebuilds the org chart from `audit.UserDirectory` in full, so the scopes follow AD from the night after a move. **Admin › Health** can refresh the last 7 days, or rebuild everything, by hand.

**Admin › Health** (`/admin/health`; before 2.4.4, `/admin`). Each pipeline step's last run and its age, marked fresh, stale or old. It also shows:

- whether the top of the organization is set and found;
- how many people sit under it;
- how many people in the data are matched to AD;
- the latest day with data;
- the last ten dashboard refreshes.

**Admin › Access.** The settings (top of the organization, minimum group size, default period, unused-after days, and what a license costs a month and in which currency, for the license recommendations), granting and removing roles, and checking one person's effective access. A new top of the organization takes effect at once.

**Admin › Reference data.** Friendly names and owners for agents: an agent shows under the name the audit log gives it (2.4.11), and a name given here shows instead. Names and sensitivity order for labels, which it records by id only (`Get-Label` lists them). Which apps count as Copilot Chat.

**Admin › Audit log.** The log, filtered by period, kind (views, refusals, exports, API calls, admin changes) and search, and exportable.

**Admin › Health** also lists the pipeline's recorded runs (`audit.PipelineRun`): each nightly or history run, how long it took, and for a failed one the step it stopped at and its last log lines.

Every admin change needs the Admin role and a valid anti-forgery token, and is written to the audit log.

## Trying it on a demo database

`dashboard/demo/New-InsightsDemo.ps1` builds a synthetic company: 2,317 people in six divisions, with six months of Copilot use, licenses, agents, labels and a few security signals. It builds it with the repository's own scripts, exactly as an install would. It also grants one director each persona role.

```powershell
.\dashboard\demo\New-InsightsDemo.ps1 -SqlServer localhost -Database CopilotInsightsDemo -TrustServerCertificate -Force -Admins 'CONTOSO\me'
cd dashboard\CopilotInsights
dotnet run --launch-profile https
```

To see the demo as one of its people, map your own Windows account to them in `appsettings.Development.json` and restart the site:

```json
"Insights": { "IdentityMap": { "CONTOSO\\me": "avery.morgan@contoso.com" } }
```

The demo's people have no SID on record, so the mapping is allowed; in a real directory, a person with a SID can only be the account with that SID. The script prints who to try:

- the CEO;
- the heads of division, who are L2;
- a manager, and someone with nobody under them;
- the persona-role holders.

## Not yet proven

- **The IIS installer** has only been parsed under Windows PowerShell 5.1. It has not been run on a Windows Server.
- **The lab's own data**, and anything at the real organization's size (18,000 people now, 40,000 expected). The demo has 2,317. The rollups are sized per active person per day, and every list is paged in SQL, but the page timings above are the demo's.
- **Kerberos on a host name, with a gMSA,** in a real domain.
- **Email through Exchange Online.** Sending was tested against a local SMTP server requiring a user name and password on ports 2525 (STARTTLS) and 4465 (implicit TLS), with the certificate check both on and off. Exchange Online's password and OAuth sign-ins follow Microsoft's documentation and have not been tried against a tenant.
- **The Windows event log source** is created by the IIS installer, which has not run on a server; without it, alerts still go by email and into the notification log.
- **Webhooks to a real Teams channel or ServiceNow instance.** The payloads were checked against their formats in unit tests; posting was not tried against a live endpoint.
- **Extracts to a network share** under a gMSA: tested only to a local folder.
- **Manager digests at the organization's size.** Each round sends at most 200 and the next round carries on; 2,000 managers take about an hour at the default five-minute interval.
