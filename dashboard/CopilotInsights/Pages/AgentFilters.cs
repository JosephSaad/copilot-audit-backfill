namespace CopilotInsights.Pages;

/// <summary>Which of the Agents filters a page offers (2.6.4).</summary>
public enum AgentFilters { None, Environment, Both }

/// <summary>The Agents filters' choices on a page (2.6.4): the environment types, and whether the kind of agent is offered too.</summary>
public sealed record AgentFilterBar(IReadOnlyList<string> Environments, bool ShowType);
