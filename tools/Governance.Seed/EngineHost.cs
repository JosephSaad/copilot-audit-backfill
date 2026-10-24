using Governance.Application;
using Governance.Application.Data;
using Governance.Application.Outbox;
using Governance.Application.Workflow;
using Governance.Domain;
using Microsoft.Extensions.Options;

namespace Governance.Seed;

/// <summary>A persona acting through the engine: one person, central scope for system-mode connections.</summary>
public sealed class PersonaPrincipal(int personId, string displayName) : ICurrentPrincipal
{
    public bool IsAuthenticated => true;
    public int PersonId { get; } = personId;
    public int? RealPersonId => null;
    public string DisplayName { get; } = displayName;
    public bool IsCentral => true;
    public IReadOnlyList<RoleGrant> Roles { get; } = Array.Empty<RoleGrant>();
    public IReadOnlySet<string> AppRoles { get; } = new HashSet<string>();
    public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();
    public IReadOnlyDictionary<int, IReadOnlySet<string>> ScopedPermissions { get; } = new Dictionary<int, IReadOnlySet<string>>();
}

/// <summary>
/// The real workflow engine wired without a DI container, so the seed and the tests drive requests exactly as
/// the web does. Each call runs in its own transaction as the given persona.
/// </summary>
public sealed class EngineHost
{
    public string ConnectionString { get; }
    public WorkflowEngine Engine { get; }
    public IClock Clock { get; }

    public EngineHost(string connectionString, IClock? clock = null)
    {
        ConnectionString = connectionString;
        Clock = clock ?? new SystemClock();
        var outbox = new OutboxWriter();
        Engine = new WorkflowEngine(Clock, new BusinessCalendarProvider(Clock), new AssignmentResolver(Clock), outbox,
            new AuditWriter(Clock), new JourneyEffects(Clock, outbox));
    }

    public Db DbFor(ICurrentPrincipal principal)
        => new(Options.Create(new DbOptions { ConnectionString = ConnectionString }), principal);

    public Task<T> AsAsync<T>(ICurrentPrincipal who, Func<Uow, Task<T>> work) => DbFor(who).InTransactionAsync(work);

    public Task AsAsync(ICurrentPrincipal who, Func<Uow, Task> work) => DbFor(who).InTransactionAsync(work);

    public Task<T> SystemAsync<T>(Func<Uow, Task<T>> work) => DbFor(SystemPrincipal.Instance).InTransactionAsync(work);

    public Task SystemAsync(Func<Uow, Task> work) => DbFor(SystemPrincipal.Instance).InTransactionAsync(work);
}
