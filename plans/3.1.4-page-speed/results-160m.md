# 3.1.4 at 160M: results (2026-11-24)

The 3.1.4 build against 3.1.3, both on the lab database with two years of 160 million interactions (210,000 people), every visit a first visit (the query cache off), every page at 7, 14, 30, 90, 180, 365 and 730 days and All data, for the whole organization, a part of 70,000 people and a team of 585. 8 processors, SQL Server 2025 Enterprise Developer at MAXDOP 6. The 15 Admin pages, one visit each, all under 1 s, are left out of the page counts; the four old addresses that redirect are left out of every count.

The goal was every page under 1 s, then 95% of pages under 2 s at their slowest. 3.1.4 was cut short of it at the user's request: 59 of 78 report pages are under 2 s at their slowest.

| | 3.1.3 | 3.1.4 |
|---|---:|---:|
| Median first visit | 0.99 s | 0.39 s |
| 90th percentile | 9.58 s | 1.90 s |
| Slowest visit | 42.0 s | 12.6 s |
| Visits over 2 s | 837 of 2,276 | 188 of 2,087 |
| Pages under 2 s at their slowest | 28 of 78 | 59 of 78 |
| Pages under 1 s at their slowest | 22 of 78 | 43 of 78 |

| Page | 3.1.3 slowest | 3.1.4 slowest |
|---|---:|---:|
| Agents in use (`/agents`) | 42.0 s | 1.7 s |
| Apps (`/apps`) | 37.5 s | 0.8 s |
| Agents costing credits (`/agents/credits`) | 27.6 s | 2.3 s |
| Forecast (`/forecasting`) | 27.4 s | 1.0 s |
| What to act on (`/leadership/act`) | 27.1 s | 8.2 s |
| Oversharing hotspots (`/compliance/sites`) | 26.4 s | 12.6 s |
| Overview (`/leadership`) | 23.7 s | 2.5 s |
| Budget and renewal (`/forecasting/budget`) | 21.7 s | 2.7 s |
| Board pack (`/leadership/boardpack`) | 21.5 s | 3.5 s |
| Models and controls (`/governance`) | 21.4 s | 3.1 s |
| Sensitive content (`/compliance`) | 21.4 s | 6.5 s |
| Better with a license (`/licensing/license-for-credits`) | 20.7 s | 1.5 s |
| Apps and agents (`/leadership/mix`) | 19.7 s | 3.6 s |
| Demand for licenses (`/licensing/demand`) | 19.6 s | 1.8 s |
| Access to labeled content (`/compliance/access`) | 19.1 s | 4.8 s |
| Funnel and trend (`/adoption`) | 17.7 s | 1.6 s |
| Engagement (`/enablement`) | 17.7 s | 1.8 s |
| Costs and savings (`/leadership/costs`) | 17.6 s | 5.1 s |
| License optimization (`/forecasting/optimize`) | 16.8 s | 1.1 s |
| Security signals (`/security`) | 13.2 s | 4.2 s |
| License inventory (`/licensing`) | 13.2 s | 1.8 s |
| Workforce (`/workforce`) | 13.1 s | 0.5 s |
| Value and time saved (`/leadership/value`) | 13.0 s | 1.2 s |
| Credit use (`/licensing/credit-use`) | 11.9 s | 3.6 s |
| Upgrade, train or revoke (`/forecasting/actions`) | 11.2 s | 0.5 s |
| Use cases (`/leadership/usecases`) | 9.3 s | 1.6 s |
| Countries and offices (`/regions`) | 9.2 s | 0.9 s |
| Usage by license (`/adoption/by-license`) | 8.4 s | 7.9 s |
| Departments (`/adoption/departments`) | 7.6 s | 0.7 s |
| Suite fit (`/licensing/suites`) | 7.6 s | 3.1 s |
| Labeled content by unit (`/compliance/units`) | 7.5 s | 6.8 s |
| Organization (`/leadership/org`) | 7.3 s | 1.5 s |
| Flagged interactions (`/security/flags`) | 6.5 s | 1.8 s |
| Use ranking (`/licensing/ranking`) | 6.2 s | 0.6 s |
| Job families (`/workforce/families`) | 4.4 s | 0.3 s |
| Agent inventory (`/agents/inventory`) | 4.3 s | 0.3 s |
| Champions (`/enablement/champions`) | 4.0 s | 0.6 s |
| Chargeback (`/licensing/chargeback`) | 3.4 s | 0.3 s |
| Overlapping licenses (`/licensing/overlap`) | 3.4 s | 1.8 s |
| License pool (`/licensing/pool`) | 3.2 s | 0.3 s |
| Working patterns (`/regions/patterns`) | 3.1 s | 2.6 s |
| New joiners (`/adoption/onboarding`) | 3.0 s | 1.0 s |
| Better on pay-as-you-go (`/licensing/payg`) | 2.8 s | 0.4 s |
| Conversation depth (`/enablement/depth`) | 2.8 s | 3.3 s |
| Managers and their teams (`/workforce/managers`) | 2.7 s | 0.5 s |
| People by engagement (`/enablement/people`) | 2.6 s | 0.7 s |
| Swap list (`/licensing/swap`) | 2.5 s | 0.9 s |
| Retention by license month (`/adoption/cohorts`) | 2.5 s | 1.0 s |
| Not started (`/adoption/not-started`) | 2.4 s | 0.2 s |
| Rollout waves (`/forecasting/waves`) | 2.1 s | 2.3 s |
| Agent hand-offs (`/agents/handoffs`) | 1.6 s | 1.4 s |
| Reservation holders (`/licensing/reservation-holders`) | 1.6 s | 1.3 s |
| Copilot reservations (`/leadership/reservations`) | 1.4 s | 1.2 s |
| Retention and habit (`/adoption/retention`) | 1.2 s | 0.5 s |
| Guests and external users (`/security/guests`) | 1.2 s | 0.6 s |
| Drops and spikes (`/adoption/anomalies`) | 1.0 s | 1.0 s |
| Copilot on non-human accounts (`/licensing/non-person`) | 0.9 s | 0.9 s |
| Fading use (`/licensing/fading`) | 0.9 s | 0.4 s |
| Unused licenses (`/licensing/unused`) | 0.9 s | 0.3 s |
| License changes (`/licensing/changes`) | 0.8 s | 0.6 s |
| Departures ahead (`/licensing/departures`) | 0.7 s | 0.8 s |
| Licenses on disabled accounts (`/licensing/leavers`) | 0.7 s | 0.6 s |
| Build and publish (`/agents/build`) | 0.6 s | 0.5 s |
| Start page (`/`) | 0.6 s | 0.2 s |
| Compare two teams (`/leadership/compare`) | 0.5 s | 0.1 s |
| Training effect (`/enablement/training`) | 0.5 s | 0.2 s |
| Person record (`/compliance/person`) | 0.5 s | 0.3 s |
| App detail (`/apps/detail`) | 0.5 s | 0.1 s |
| Credit chargeback (`/leadership/credit-chargeback`) | 0.4 s | 0.1 s |
| Credit commitment (`/forecasting/credit-commitment`) | 0.4 s | 0.1 s |
| License action outcomes (`/forecasting/outcomes`) | 0.4 s | 0.1 s |
| AI apps beyond Copilot (`/governance/ai-apps`) | 0.4 s | 0.1 s |
| Agents without an owner (`/agents/owners`) | 0.4 s | 0.1 s |
| Agent detail (`/agents/detail`) | 0.4 s | 0.1 s |
| How figures are calculated (`/leadership/assumptions`) | 0.4 s | 0.1 s |
| Child agent usage (`/agents/children`) | 0.4 s | 0.1 s |
| Targets (`/adoption/targets`) | 0.4 s | 0.1 s |
| Agent checks (`/agents/checks`) | 0.4 s | 0.1 s |
