namespace Recall.Server.Databases;

using System.Diagnostics;
using Auditing;
using Domain;
using Domain.Agents;
using Domain.AuditEntries;
using Domain.Corrections;
using Domain.People;
using Domain.Sessions;
using Domain.Sync;
using Domain.Tenants;
using Domain.Users;
using Domain.UserSettings;
// new-feature:db-usings
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Sqlite;

public class AppDbContext(
    DbContextOptions<AppDbContext> options,
    TimeProvider timeProvider,
    ICurrentUserService currentUserService,
    ICallerIdentityResolver callerIdentityResolver,
    ITenantScopeProvider tenantScopeProvider,
    DomainEventDispatcher domainEventDispatcher) : DbContext(options)
{
    private const int MaxDomainEventRounds = 10;

    #region DbSet Region
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentPermission> AgentPermissions => Set<AgentPermission>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<RawRevision> RawRevisions => Set<RawRevision>();
    public DbSet<CleanedRevision> CleanedRevisions => Set<CleanedRevision>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Correction> Corrections => Set<Correction>();
    public DbSet<UserSetting> UserSettings => Set<UserSetting>();
    public DbSet<SyncChange> SyncChanges => Set<SyncChange>();
    // new-feature:db-sets
    #endregion

    private TenantScope? _tenantScope;
    private TenantScope? _narrowedTenantScope;
    private SaveStage _saveStage = SaveStage.Idle;
    private string? _requestName;
    private readonly List<AuditAction> _recordedActions = [];

    /// <summary>
    /// The tenants whose rows the current caller can see. The tenant query filter reads it for each query.
    /// The context gets the scope once and keeps it, because the caller of a DI scope does not change.
    /// While the handlers of a domain event run, it is the tenant of the event. In <see cref="InTenantScopeAsync{T}"/>,
    /// it is the tenant of that call.
    /// </summary>
    /// <remarks>
    /// The context is not pooled. If you add DbContext pooling, clear this value when the pool resets the context.
    /// </remarks>
    public TenantScope CurrentTenantScope =>
        _narrowedTenantScope ?? (_tenantScope ??= tenantScopeProvider.GetCurrentScope());

    public string? CurrentJournalOwnerId => callerIdentityResolver.Current.JournalOwnerId;

    internal bool IsRunningDomainEventHandlers => _saveStage == SaveStage.Dispatching;

    /// <summary>
    /// Makes the next query get the tenant scope again. Call it only when code changes the caller of a live
    /// DI scope, for example a test that changes the current user after a query.
    /// </summary>
    public void RefreshTenantScope() => _tenantScope = null;

    internal async Task<T> InTenantScopeAsync<T>(string tenantId, Func<Task<T>> work)
    {
        var scope = CurrentTenantScope;
        if (!scope.IncludesAllTenants && scope.TenantId != tenantId)
            throw new InvalidOperationException($"The caller cannot see tenant {tenantId}, so its scope cannot become the scope of that tenant.");

        var outer = _narrowedTenantScope;
        _narrowedTenantScope = TenantScope.ForTenant(tenantId);
        try
        {
            return await work();
        }
        finally
        {
            _narrowedTenantScope = outer;
        }
    }

    /// <summary>
    /// Names the MediatR request whose saves this context writes, until the result is disposed. The outermost request
    /// wins: a nested Send shares this context and its saves, so it keeps the outer name.
    /// </summary>
    internal EnteredRequest EnterRequest(string requestName)
    {
        var entered = new EnteredRequest(this, _requestName);
        _requestName ??= requestName;
        return entered;
    }

    /// <summary>
    /// Adds the action to the audit log of the next save, in the same transaction and the same record as the changes of
    /// that save. Save also when the handler changes no data. <see cref="Resources.AuditRequestBehavior{TRequest,TResponse}"/>
    /// throws when a request records an action and does not save it.
    /// </summary>
    public void RecordAction(AuditAction action) => _recordedActions.Add(action);

    internal bool HasUnsavedActions => _recordedActions.Count > 0;

    internal RootLock? HeldRootLock { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Automatically discovers and applies all IEntityTypeConfiguration<T> implementations in the assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTextConverter>();
        configurationBuilder.DefaultTypeMapping<DateTimeOffset>().HasConversion<UtcTextConverter>();
        configurationBuilder.MapEntityIdToField();
        configurationBuilder.MapVersionToConcurrencyToken();
        configurationBuilder.FilterSoftDeletedRecords();
        configurationBuilder.FilterByTenant(this);
        configurationBuilder.FilterByJournalOwner(this);
        configurationBuilder.MapConstraintErrors();
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        optionsBuilder.AddInterceptors(WriteGuard.Instance);
    }

    /// <summary>
    /// Refuses the save. The save runs async work, so <see cref="AppDbContext"/> has no sync save.
    /// <see cref="DbContext.SaveChanges()"/> also calls this overload.
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
        => throw new NotSupportedException("AppDbContext has no sync save. Call SaveChangesAsync.");

    /// <summary>
    /// Runs the handlers of the queued domain events, then saves the changes and the handler changes in one
    /// transaction. <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> also calls this overload.
    /// A failed save ends the context, so a retry must use a new DI scope. Only
    /// <see cref="Resources.RaceRetryBehavior{TRequest,TResponse}"/> runs a command again in the same scope.
    /// </summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        RefuseSave();
        return EndOnFailure(SaveInStages(acceptAllChangesOnSuccess, cancellationToken));
    }

    // The tracker of a failed write can hold the stamps and the audit entries of a write that did not commit.
    private async Task<int> EndOnFailure(Task<int> write)
    {
        try
        {
            var result = await write;
            _saveStage = SaveStage.Idle;
            return result;
        }
        catch
        {
            _saveStage = SaveStage.Broken;
            throw;
        }
    }

    /// <summary>
    /// Forgets the work of a command that lost a race, so <see cref="Resources.RaceRetryBehavior{TRequest,TResponse}"/>
    /// can run it again in the same DI scope. The lost save rolled back, so the database has none of its changes. It
    /// clears the tracked entities and the recorded audit actions, and it ends the refusal of a save after a failure.
    /// </summary>
    internal void DiscardLostRace()
    {
        if (_saveStage is SaveStage.Dispatching or SaveStage.Saving)
            throw new InvalidOperationException("A lost race can be discarded only after its save ends.");

        ChangeTracker.Clear();
        _recordedActions.Clear();
        _saveStage = SaveStage.Idle;
    }

    private async Task<int> SaveInStages(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        _saveStage = SaveStage.Dispatching;
        await RunDomainEventHandlers(cancellationToken);

        _saveStage = SaveStage.Saving;
        var auditEntries = PrepareWrite(await callerIdentityResolver.GetAsync(cancellationToken));
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken) - auditEntries.Count;
        }
        catch (DbUpdateException exception)
        {
            // Not in a filter, because a filter loses an exception that it throws.
            if (DatabaseConflict.From(exception, Model) is { } conflict)
                throw conflict;
            throw;
        }
    }

    private void RefuseSave()
    {
        switch (_saveStage)
        {
            case SaveStage.Broken:
                throw new InvalidOperationException(
                    "An earlier save on this AppDbContext failed, so it cannot save again. Use a new DI scope.");
            case SaveStage.Dispatching or SaveStage.Saving:
                throw new InvalidOperationException(
                    "A domain event handler must not call SaveChangesAsync. The save that runs the handler saves its changes.");
        }

        if (Database.CurrentTransaction is not null)
            throw new NotSupportedException(
                "AppDbContext does not save inside a transaction that you opened. One save is one transaction.");
    }

    private async Task RunDomainEventHandlers(CancellationToken cancellationToken)
    {
        for (var round = 1; ; round++)
        {
            var queued = ChangeTracker.Entries<BaseEntity>()
                .SelectMany(entry => entry.Entity.TakeDomainEvents().Select(queuedEvent => (entry, queuedEvent)))
                .OrderBy(item => item.queuedEvent.Sequence)
                .ToList();
            if (queued.Count == 0)
                return;

            if (round > MaxDomainEventRounds)
            {
                var eventTypes = string.Join(", ", queued.Select(item => item.queuedEvent.Event.GetType().Name).Distinct());
                throw new InvalidOperationException(
                    $"The domain event handlers queued new events for {MaxDomainEventRounds} rounds, so they form a cycle. " +
                    $"The events of the next round: {eventTypes}.");
            }

            var outer = _narrowedTenantScope;
            foreach (var (entry, queuedEvent) in queued)
            {
                _narrowedTenantScope = TenantQueryFilterExtension.TenantIdOfDomainEvent(entry) is { } tenantId
                    ? TenantScope.ForTenant(tenantId)
                    : outer;
                try
                {
                    await domainEventDispatcher.DispatchAsync(queuedEvent.Event, this, cancellationToken);
                }
                finally
                {
                    _narrowedTenantScope = outer;
                }
            }
        }
    }

    private IReadOnlyList<AuditEntry> PrepareWrite(CallerIdentity caller)
    {
        // Entities that code removed from a collection become Deleted here.
        ChangeTracker.DetectChanges();
        RefuseAuditEntriesNotFromCapture();

        var auditModel = AuditModel.Of(Model);
        var aggregates = TrackedAggregates.Index(ChangeTracker, auditModel);
        aggregates.GuardRootsOf(_recordedActions);
        TouchOwnersOfChangedChildren(aggregates);

        var now = timeProvider.GetUtcNow();
        StampEntities(now);

        var actor = AuditActor.Of(currentUserService.UserIdentifier, currentUserService.ClientId, caller);
        var save = SaveFacts.From(now, actor, _requestName, Activity.Current?.TraceId.ToHexString());
        var auditEntries = AuditDiff.Compose(save, AuditDiff.Changes(AuditSnapshots.Take(aggregates, auditModel)),
            _recordedActions);
        _recordedActions.Clear();
        Set<AuditEntry>().AddRange(auditEntries);
        return auditEntries;
    }

    private void RefuseAuditEntriesNotFromCapture()
    {
        if (ChangeTracker.Entries<AuditEntry>().Any(e => e.State != EntityState.Unchanged))
            throw new InvalidOperationException(
                "Only the save writes audit entries. Do not add, change, or remove an AuditEntry.");
    }

    // A child that moves to another owner, or to no owner, also changes the aggregate of the owner that it leaves.
    private static void TouchOwnersOfChangedChildren(TrackedAggregates aggregates)
    {
        var changed = new Queue<EntityEntry<BaseEntity>>(aggregates.Changed);
        while (changed.TryDequeue(out var child))
        {
            foreach (var owner in new[] { aggregates.OwnerOf(child), aggregates.FormerOwnerOf(child) })
            {
                if (owner is not { State: EntityState.Unchanged })
                    continue;

                owner.Property(e => e.LastModifiedOn).IsModified = true;
                changed.Enqueue(owner);
            }
        }
    }

    private void StampEntities(DateTimeOffset now)
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property(e => e.Version).CurrentValue = 1;
                    entry.Entity.UpdateLastModifiedOn(now);
                    break;

                case EntityState.Modified:
                    IncrementVersion(entry);
                    entry.Entity.UpdateLastModifiedOn(now);
                    break;

                case EntityState.Deleted:
                    if (entry.Entity is IDeletedWithEvent { DeleteQueued: false })
                        throw new InvalidOperationException(entry.Entity is User
                            ? "Remove a user with Remove(user.Delete()), so that the save queues UserDeleted."
                            : $"Remove a {entry.Metadata.ClrType.Name} with Remove(entity.Delete()), so that the save queues its delete event.");

                    entry.Entity.UpdateIsDeleted(true);

                    var isHardDelete = entry.Entity is IJournalOwned;
                    if (isHardDelete)
                        break;

                    entry.State = EntityState.Modified;
                    IncrementVersion(entry);
                    entry.Entity.UpdateLastModifiedOn(now);
                    break;
            }
        }
    }

    private static void IncrementVersion(EntityEntry<BaseEntity> entry)
    {
        var version = entry.Property(e => e.Version);
        version.CurrentValue = version.OriginalValue + 1;
    }

    /// <summary>Gives the context back the name of the outer request when disposed.</summary>
    internal sealed class EnteredRequest(AppDbContext context, string? outerRequestName) : IDisposable
    {
        public bool IsOutermost => outerRequestName is null;

        public void Dispose() => context._requestName = outerRequestName;
    }

    private enum SaveStage
    {
        Idle,
        Dispatching,
        Saving,
        Broken,
    }
}
