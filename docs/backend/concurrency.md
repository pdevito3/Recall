# Concurrency

Two requests can change the same rows at the same time. Each command picks one of three tools for this race: a version, a run again, or a lock. Pick the tool before you write the handler.

| Tool | Use it when | A lost race gives |
|------|-------------|-------------------|
| A version | The user saw data and decided on it, for example in an edit form | 409 stale version. The caller loads the record again and repeats the change |
| A run again | The command sends no version, and the server decides it from the rows alone | Nothing. The command runs again on the new rows. After 20 lost runs, 409 concurrent change |
| A lock | The rule of the command reads rows that the command does not write | Nothing. The second request waits for the first |

A create of a new aggregate root writes no row that another request can change, so it needs no version and no run again. It needs a lock only when its rule reads other rows, such as a booking that checks its clinic. A unique value that two creates add at the same time gets a 409 from the unique index (see [Error responses](../backend.md#error-responses)). When the index has `ForStaleVersion`, the create gets the stale-version 409.

## Versions

Each entity has `Version`, an integer column of its row. `MapVersionToConcurrencyToken` maps it for each entity, so a new entity needs no configuration for it. The save sets it to 1 on an add and adds 1 on each update and soft delete. EF Core adds `WHERE version = @loaded` to each update. When another save changed the row after the load, the save throws a `StaleVersionException`. The reasons for this design are in [ADR 0008](../adr/0008-optimistic-concurrency-with-xmin.md).

- A save that adds, changes, or removes an owned child also updates the owner row. Thus the version of the root guards the whole aggregate. An owned child is the dependent of a `Cascade` foreign key (see [entity-configurations.md](entity-configurations.md)). Change a child only through its loaded owner. The save throws when the owner of a changed child is not tracked. A child that moves to another owner, or to no owner, also updates the row of the owner that it leaves, so load that owner too.
- The read DTO of a root shows `version`. A PUT body that replaces the fields of an edit form has a required `version`, and the entity method calls `GuardVersion` first (see [domain-entities.md](domain-entities.md#versions)).
- `Version` is an opaque number. Compare it only for equality. A list cannot filter or sort on `version`, and `ApplyFilters` and `ApplySortOrder` return 400 for it. They also return 400 for `allowedActions`, because the handler adds it after the page loads. A list has no use for the order or the value of a version.

Do not catch a `StaleVersionException` of a command that sends a version, and do not send the command again with the new version. The caller did not see the change of the other request, so a retry overwrites it.

A unique index can also tell that another user changed the record, for example an index that permits one current revision for each document. Give such an index `ForStaleVersion` (see [entity-configurations.md](entity-configurations.md#unique-indexes)). When the index stops a save, `AppDbContext` throws a `StaleVersionException`. The caller gets the same 409 as for an old version, with the same `type`, `title`, `detail`, and `errors`. Thus the SPA handles the two in the same way. A command with `IRunsAgainWhenRaced` runs again after such a save, the same as after a failed version check.

### A version of part of a row

A save can guard only part of a row when other writes move the `Version` of that row with no relation to that part. A Draft save moves the `Version` of its Session, and it does not make a metadata save stale. So a metadata save sends `metadataVersion`, a counter on the Session that only an applied metadata change moves (SES-32). A stale value throws `StaleMetadataVersionException`, which gives the same 409 as a stale version, with `metadataVersion` in `errors`. It is not a `StaleVersionException`, so `RaceRetryBehavior` does not catch it.

`SetSessionMetadata` keeps `IRunsAgainWhenRaced`, because a lost race on the row with a Draft save says nothing about the metadata. The next run reads the row again and checks `metadataVersion` again, so a rival metadata save gives the 409 on that run. Rule 5 of the run-again rules does not stop this increment, because each run adds one to the row that it read, and only after the check of the base.

Add such a counter only when writes that do not conflict with the form must share the row. In other cases, send `Version`.

## Run again

A command for one intent, such as a new permission, a role change, or a delete, sends no version. The version check of the owner row still stops its race between the load and the save. But the caller decided nothing on the old rows, so a 409 that says "Reload it" does not help the caller. Mark the command with `IRunsAgainWhenRaced` instead:

<!-- code: Recall.Server/Domain/Users/Features/AddUserPermission/AddUserPermission.cs -->
```csharp
public sealed record Command(string Id, [property: RequiredField] string Permission) : IRequest<UserDto>, IRunsAgainWhenRaced;
```

`RaceRetryBehavior` catches the `StaleVersionException` of a marked command. It calls `AppDbContext.DiscardLostRace`, which clears the tracked entities and the recorded audit actions. Then it waits for a short random time and runs the handler again in the same DI scope. The lost save rolled back, so the database has none of its changes. The caller, the validation, and the request span stay those of the first run. Each lost run that runs again adds a `race.lost` event with `app.race.run` to the request span. After 20 lost runs, the behavior adds a `race.exhausted` event with `app.race.run` and throws a `ConcurrentChangeException`.

Obey these rules for a marked command. A test enforces the first two.

1. The request has no `Version`, also in a DTO in it. With a version, a lost race is a stale version on each run. The behavior then runs the command 20 times, and the caller gets a concurrent change, not the stale version that it must get. `RunAgainRequestTests` fails on such a request. The one exception is a version of part of a row, as in [A version of part of a row](#a-version-of-part-of-a-row), because its stale value does not throw a `StaleVersionException`.
2. Send the command only from an entrypoint (HTTP or MCP), not from a handler or a domain event handler. `DiscardLostRace` clears each tracked change of the DI scope, also the changes of the handler that sent the command. `EntrypointBoundaryTests` fails when core code creates a marked command.
3. Do the work that leaves the process, such as a log of the change, a metric, or a call to another service, after the save. The handler runs again from the start, so the work before the save runs one time for each run.
4. The handler saves one time. A second run after a lost second save would apply the first save again.
5. Do not mark a command that adds to a value, such as an increment.

The tests do not see these breaks of the rules. Check them in the review:

- A `Version` property with a different name, such as `RowVersion`. `RunAgainRequestTests` looks only for the name `Version`. `metadataVersion` is the one allowed case, because it has its own exception.
- A copy of a marked record with `with` in core code. `EntrypointBoundaryTests` does not count the copy method of a record.
- A background job in the shared plumbing or the composition root that sends a marked command. `EntrypointBoundaryTests` reads only the core types. `DiscardLostRace` then clears the tracked changes of the job.

A Session is hard-deleted, and a new Revision inserts before the version check of its Session. So `DatabaseConflict` also reads a missing parent of a Revision as a lost race. The next run gives 404. A missing parent of any other row is a server fault, and `a_session_with_an_owner_that_never_existed_is_a_server_fault_and_not_a_lost_race` proves it. See [ADR 0020](../adr/0020-journal-rules-in-the-database.md). A create in the journal does not read its owner again in the work. The owner purge trigger refuses the row of a deleted owner. `an_add_that_a_delete_of_its_owner_beat_saves_no_row` proves it for a delete that commits during the add, and `an_add_and_a_delete_of_its_owner_at_one_time_leave_no_row` proves it for the two at one time, in either order.

A Draft save loads the Session, and then it looks for the Revision of a replay in a second query. When that Revision has a number above the latest number of the load, the save of the Revision committed between the two queries. The handler then throws a `StaleVersionException` and runs again on a new load, so a reply never names a `latestRevision` below its `draftRevision`. The two `..._replay_whose_first_try_saved_after_the_load_...` tests in `JournalRaceTests` prove it.

Each marked command has a race test (see [Race tests](../backend.md#race-tests)). The test pauses the command after its load, commits a rival write to the same aggregate, and checks that both changes saved.

## Lock

A version guards the rows that a command writes. A rule over a set of rows can break with no conflict on any one row. For example, "two open bookings of a room do not overlap", or "a clinic with an open appointment cannot be deactivated". A booking reads the clinic and writes an appointment. A deactivation counts the open appointments and writes the clinic. Each reads the rows that the other writes, so no version catches the race, and a run again does not help.

`RootLock` in `Databases/` is a lock in the process on one aggregate root. It has no database state, because Recall runs one process per data file. The template has three rules of this kind. First, `DeleteTenant` deletes each row that the tenant owns, and `AddAgent` and `AddUser` add an agent or a user to the tenant. The add of each new tenant-owned aggregate root takes the shared lock in the same way. `TenantLockTests` fails when a type adds a tenant-owned root, or saves after such an add, outside the work of `RunSharedAsync`. A child add changes the version of its owner instead, and the delete loads each owner before its children. Without the lock, an agent or a user that is saved during `DeleteTenant` stays live in the deleted tenant. A live user in a deleted tenant keeps its identifier, so that person cannot sign up again. Second, `DeleteUser`, `UpdateUserRole`, and `RemoveUserPermission` hold the exclusive lock, because a tenant keeps an Admin who can sign in and manage access, and that rule reads the other Admins. Third, `DeleteTenant` also holds the exclusive `RootLock.Install`, because an install keeps a live tenant, and that rule reads the other tenants. Two deletes of two tenants hold two different tenant locks, so only a lock with one key for the install can keep them apart. Each one runs its reads, its checks, and its save in the work of the lock:

<!-- code: Recall.Server/Domain/Agents/Features/AddAgent/AddAgent.cs -->
```csharp
var added = await RootLock.Tenant.RunSharedAsync(dbContext, tenantId, async ct =>
{
    // ...
    var agent = Agent.Create(request.Dto.ToAgentForCreation(tenantId), caller);
    // ...
    await dbContext.Agents.AddAsync(agent, ct);
    await dbContext.SaveChangesAsync(ct);
    return agent;
}, cancellationToken);
```

For a new rule, add a field such as `RootLock.Clinic` with the area name, and obey these rules:

1. Take the shared lock (`RunSharedAsync`) in each write that can add to the set, such as a booking or an agent add. Take the exclusive lock (`RunExclusiveAsync`) in each write that needs the set to stay as it read it, such as a deactivation or a tenant delete. Bookings then do not wait on each other, and a deactivation waits for the bookings that started before it. If the rule is between two adds, such as no overlap of two bookings, each add takes the exclusive lock, because two holders of the shared lock do not see the rows of each other.
2. Do the first read of the rule in the work, and save in the work. The lock is free after the work. Check the root again in the work, also when the handler checked it before. For example, `AddAgent` checks that the tenant exists in the work, also for the tenant of the caller.
3. Do not change an entity or record an action before the work. `RootLock` throws if the context has changes, because the reads of those changes had no lock.
4. Take one `RootLock` in a command. `RootLock` throws when the request already holds a lock. A second lock would wait for itself behind the next waiter, and two locks in a different order can deadlock. The one exception is `RootLock.Install` inside `RootLock.Tenant`, which `LastTenant.RunWithLocksAsync` takes. Nothing else takes `RootLock.Install`, and `RootLock` throws on the other order.
5. Write a race test that waits with `PauseAtQuery.WaitUntilBlockedAsync` until the rival waits on the lock, with `RootLock.HasWaiter`. See `TenantDeleteRaceTests.cs`.

`RootLock` does these steps for you:

- Requests get the lock in the order they ask, so a stream of shared holders cannot starve an exclusive request. A canceled wait leaves the queue, and the requests behind it continue.
- The request span has the `app.lock.mode` tag, `shared` or `exclusive`. A wait adds a span named for the root, such as `tenants.lock_wait`. A lock with no wait adds no span.

A command that is marked `IRunsAgainWhenRaced` can take a lock. A lost race throws out of the work, the lock is freed, and the command runs again and takes the lock again. `DeleteTenant` does this.

`InitiateUserAndTenant` takes no lock. It adds a new tenant, so no delete of that tenant can run at the same time.

`DeleteTenant` queues `UserDeleted` for each user, so a handler in another domain can change its link to the user. A feature that adds a link to a user takes the shared `RootLock.Tenant` of the tenant of that user, not a lock of its own. `DeleteTenant` and `DeleteUser` take the exclusive lock of the same tenant. Thus each command holds one tenant lock, as rule 4 says. See rule 11 of [domain-events.md](domain-events.md#rules).

These options do not fit:

- **A transaction around the checks and the save.** One save is one transaction ([ADR 0010](../adr/0010-domain-events-in-the-save-transaction.md)), and `AppDbContext` refuses a save inside a transaction that the handler opens.
- **A write to the shared root row in each write that adds to the set, so that its version catches the race.** Each booking of a clinic then conflicts with each other booking of that clinic, and the caller gets a 409 for a booking that had no problem.


## Writer line

SQLite has one write lock for a file, and it has no line for that lock. A writer that finds the lock taken sleeps and tries again, and the sleep grows to 100 ms. A writer that just came tries far more often than a writer that has waited for a long time. When the lock is seldom free, the oldest writer can lose each try until `busy_timeout` ends, and it gets `database is locked`. Its wait also blocks a thread.

`SqliteWriterLine` gives the writers of one process a line for each data file. The first writer goes first, and a writer that waits holds no thread. `SqliteWriterLineTests` holds this order, because the docs of `SemaphoreSlim` do not promise it. `AppDbContext` takes the turn around the write of a save, after the domain event handlers. The interceptor takes the turn of async `ExecuteUpdate` and `ExecuteDelete`. A write in an open transaction takes no turn, because the transaction holds the lock or the turn of its save.

BackWave and `WalScrub` have their own connections and timeouts, and they stay outside the line. When each write stalls for about 100 ms or more and the line is never empty, a BackWave worker can lose each try for 30 s. It logs a store fault and tries again on the next tick. A write inside a turn must not start another write on a new context, because the second write waits for the first.
