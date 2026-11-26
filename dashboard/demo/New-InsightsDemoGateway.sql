/*
    Copilot Insights demo: model tokens through the AI gateway, the spending limits read at their sources, and the
    agent rules, for Leadership > Model tokens by business unit, Forecasting > Spend limits and Governance > Agent
    policies. Run after New-InsightsDemoCosts.sql (the Foundry usage it sits beside). Deterministic, relative to the
    newest Foundry day loaded, like the rest of the demo.

    The story:
      - Two gateways (contoso-apim-eus, contoso-apim-weu) carry about seven tenths of Foundry's tokens; the rest
        is called directly, so the bypass shows.
      - Eight business-unit values: five cost-center codes routed to an L2 and L3 executive pair, a sixth routed
        to an L2's whole unit, one value that is a person's sign-in (the org chart gives its unit), one legacy code
        nobody has routed, and a little traffic the gateway stamped no business unit for.
      - Three models, the small one growing; one unit doubling in the last month.
*/
SET NOCOUNT ON;

DECLARE @end DATE = ISNULL((SELECT MAX([Day]) FROM audit.FoundryUsageDay), DATEADD(DAY, -2, CAST(SYSUTCDATETIME() AS DATE)));
DECLARE @days INT = 120;

/* The units: the five largest L3 executives' units and their L2s, and one more L2's whole unit. */
DECLARE @unit TABLE (Code NVARCHAR(256) PRIMARY KEY, L2 NVARCHAR(256), L3 NVARCHAR(256), Weight FLOAT, Growth FLOAT);
INSERT INTO @unit (Code, L2, L3, Weight, Growth)
SELECT CONCAT(N'CC-', 1000 + ROW_NUMBER() OVER (ORDER BY p.PeopleBelow DESC, p.Upn)), m.Upn, p.Upn,
       CASE ROW_NUMBER() OVER (ORDER BY p.PeopleBelow DESC, p.Upn) WHEN 1 THEN 0.30 WHEN 2 THEN 0.20 WHEN 3 THEN 0.12 WHEN 4 THEN 0.08 ELSE 0.05 END,
       CASE ROW_NUMBER() OVER (ORDER BY p.PeopleBelow DESC, p.Upn) WHEN 3 THEN 1.0 ELSE 0.25 END
FROM (SELECT TOP (5) * FROM dash.Person WHERE [Level] = 3 ORDER BY PeopleBelow DESC, Upn) AS p
JOIN dash.Person AS m ON m.Upn = p.ManagerUpn;
INSERT INTO @unit (Code, L2, L3, Weight, Growth)
SELECT TOP (1) N'CC-2000', p.Upn, NULL, 0.07, 0.25 FROM dash.Person AS p WHERE p.[Level] = 2 AND p.Upn NOT IN (SELECT L2 FROM @unit) ORDER BY p.PeopleBelow DESC, p.Upn;
/* A person's sign-in, stamped by an app that signs its users in: a level-5 person in the largest unit. */
INSERT INTO @unit (Code, L2, L3, Weight, Growth)
SELECT TOP (1) c.DescendantUpn, NULL, NULL, 0.04, 0.25
FROM dash.OrgClosure AS c JOIN dash.Person AS p ON p.Upn = c.DescendantUpn
WHERE c.AncestorUpn = (SELECT TOP (1) L3 FROM @unit ORDER BY Weight DESC) AND p.[Level] = 5 ORDER BY p.Upn;
INSERT INTO @unit (Code, L2, L3, Weight, Growth) VALUES (N'BU-LEGACY-07', NULL, NULL, 0.06, 0.0), (N'', NULL, NULL, 0.03, 0.0);

DECLARE @model TABLE (Model NVARCHAR(200), Share FLOAT, Growth FLOAT, Completion FLOAT);
INSERT INTO @model VALUES (N'gpt-4o', 0.55, 0.0, 0.30), (N'gpt-4o-mini', 0.35, 0.8, 0.35), (N'text-embedding-3-large', 0.10, 0.1, 0.0);

/* Seven tenths of Foundry's tokens a day, or a fixed day's worth where Foundry is not loaded. */
DECLARE @foundry TABLE ([Day] DATE PRIMARY KEY, Tokens FLOAT);
INSERT INTO @foundry SELECT [Day], SUM(CAST(ISNULL(TotalTokens, 0) AS FLOAT)) FROM audit.FoundryUsageDay WHERE [Day] > DATEADD(DAY, -@days, @end) GROUP BY [Day];
IF NOT EXISTS (SELECT 1 FROM @foundry)
    INSERT INTO @foundry SELECT DATEADD(DAY, -n, @end), 40000000 FROM (SELECT TOP (@days) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n FROM sys.all_objects) AS x;

DELETE FROM audit.GatewayTokenDay WHERE Gateway IN (N'contoso-apim-eus', N'contoso-apim-weu');
INSERT INTO audit.GatewayTokenDay (Gateway, [Day], BusinessUnit, Model, Agent, Api, PromptTokens, CompletionTokens, TotalTokens, Calls)
SELECT g.Gateway, f.[Day], u.Code, m.Model, N'', N'openai',
       CAST(v.T * (1 - m.Completion) AS BIGINT), CAST(v.T * m.Completion AS BIGINT), CAST(v.T AS BIGINT), CAST(v.T / 2400 AS BIGINT) + 1
FROM @foundry AS f
CROSS JOIN (VALUES (N'contoso-apim-eus', 0.75), (N'contoso-apim-weu', 0.25)) AS g (Gateway, Share)
CROSS JOIN @unit AS u
CROSS JOIN @model AS m
/* Each unit's and model's growth over the period, a weekday's work, and a little day-to-day noise. */
CROSS APPLY (SELECT 1.0 - DATEDIFF(DAY, f.[Day], @end) / CAST(@days AS FLOAT) AS Progress) AS pr
CROSS APPLY (SELECT f.Tokens * 0.7 * g.Share * u.Weight * m.Share
                    * (1 + u.Growth * pr.Progress) * (1 + m.Growth * pr.Progress) / 1.6
                    * CASE WHEN DATEPART(WEEKDAY, f.[Day]) IN (1, 7) THEN 0.35 ELSE 1.0 END
                    * (0.85 + 0.3 * (ABS(CHECKSUM(u.Code, m.Model, f.[Day])) % 100) / 100.0) AS T) AS v
WHERE v.T >= 1;

/* The routes: the cost-center codes, from the start of the history. */
DELETE FROM dash.TokenRoute WHERE UpdatedBy = N'demo';
INSERT INTO dash.TokenRoute (GatewayValue, L2Upn, L3Upn, EffectiveFrom, Note, UpdatedBy)
SELECT Code, L2, L3, DATEADD(DAY, -@days, @end), N'Cost center code the gateway stamps from the calling app''s subscription', N'demo'
FROM @unit WHERE L2 IS NOT NULL;

/* The spending limits as their loaders would read them: Copilot spending policies (Graph), the gateway's token
   limits and GitHub budgets. One environment's policy is projected to be reached this month, the largest cost
   center's monthly token quota is reached, and the GitHub enterprise budget is running over. */
DELETE FROM audit.SpendLimitSource WHERE ExternalId LIKE N'demo-%';
INSERT INTO audit.SpendLimitSource (Source, ExternalId, Kind, Name, ScopeType, Scope, Unit, Amount, Period, PerUser, Raw)
VALUES ('Graph', N'demo-policy-all', 'CopilotUser', N'Every user: Copilot Credits a person', 'AllUsers', NULL, 'Credits', 500, 'Monthly', 1, NULL),
       ('Graph', N'demo-policy-pilot', 'CopilotUser', N'Sales agents pilot: Copilot Credits a person', 'Group', N'Sales Copilot pilot', 'Credits', 1500, 'Monthly', 1, NULL),
       ('Graph', N'demo-policy-hr', 'CopilotResource', N'HR Services environment', 'Environment', N'HR Services', 'Credits', 55000, 'Monthly', 0, NULL),
       ('Graph', N'demo-policy-care', 'CopilotResource', N'Customer Care environment', 'Environment', N'Customer Care', 'Credits', 30000, 'Monthly', 0, NULL),
       ('Graph', N'demo-policy-benefits', 'CopilotResource', N'Benefits helper agent', 'Agent', N'Benefits helper', 'Credits', 5000, 'Monthly', 0, NULL),
       ('Gateway', N'demo-apim-eus#0#GatewayQuota', 'GatewayQuota', N'contoso-apim-eus / all APIs', 'CounterKey', N'@(context.Request.Headers.GetValueOrDefault("x-cost-center", ""))', 'Tokens', 800000000, 'Monthly', 1, NULL),
       ('Gateway', N'demo-apim-eus#0#GatewayRate', 'GatewayRate', N'contoso-apim-eus / all APIs', 'CounterKey', N'@(context.Request.Headers.GetValueOrDefault("x-cost-center", ""))', 'Tokens', 200000, 'Minute', 1, NULL),
       ('Gateway', N'demo-apim-weu#1#GatewayQuota', 'GatewayQuota', N'contoso-apim-weu / openai', 'CounterKey', N'@(context.Request.Headers.GetValueOrDefault("x-cost-center", ""))', 'Tokens', 12000000, 'Daily', 1, NULL),
       ('GitHub', N'demo-budget-enterprise', 'GitHubBudget', N'contoso enterprise Copilot', 'Enterprise', N'contoso', 'USD', 6000, 'Monthly', 0, NULL),
       ('GitHub', N'demo-budget-eng', 'GitHubBudget', N'contoso-eng organization', 'Organization', N'contoso-eng', 'USD', 9000, 'Monthly', 0, NULL);
EXEC dash.usp_SyncSpendLimits;
/* An administrator's: a limit entered for an environment no policy covers yet, and the units of the others. */
DELETE FROM dash.SpendLimit WHERE Source = 'Entered' AND UpdatedBy = N'demo';
INSERT INTO dash.SpendLimit (Source, Kind, Name, ScopeType, Scope, Unit, Amount, Period, Note, UpdatedBy)
VALUES ('Entered', 'CopilotResource', N'Retail Banking Apps environment (agreed with Finance)', 'Environment', N'Retail Banking Apps', 'Credits', 90000, 'Monthly', N'Set in the admin center after the quarter review', N'demo');
UPDATE l SET L2Upn = u.L2, L3Upn = u.L3, WarnAtPercent = 80
FROM dash.SpendLimit AS l JOIN @unit AS u ON u.Code = N'CC-1001'
WHERE l.ExternalId IN (N'demo-apim-eus#0#GatewayQuota', N'demo-policy-hr');

/* The agent rules turned on, so the nightly check (or Check the rules now) proposes what they find. Actions in
   Microsoft 365 stay off (AgentActionsEnabled 0): the demo has no agent actions app. */
UPDATE dash.AgentPolicyRule SET Enabled = 1, UpdatedBy = N'demo', UpdatedUtc = SYSUTCDATETIME() WHERE Enabled = 0;
