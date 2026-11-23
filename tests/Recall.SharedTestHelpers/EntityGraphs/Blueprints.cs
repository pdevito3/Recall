namespace Recall.SharedTestHelpers.EntityGraphs;

using Bogus;
using Recall.Server.Domain.Agents;
using Recall.Server.Domain.Agents.Models;
using Recall.Server.Domain.Corrections;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Tenants.Models;
using Recall.Server.Domain.TimeZones;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.Users.Models;
using Recall.SharedTestHelpers.EntityGraphs.Core;
using Recall.SharedTestHelpers.Fakes.Agent;
using Recall.SharedTestHelpers.Fakes.Tenant;
using Recall.SharedTestHelpers.Fakes.User;

/// <summary>
/// One blueprint per entity. Each factory calls the real domain factory, so every built entity
/// passes the same invariants as production code. Never set entity state any other way.
/// </summary>
/// <remarks>
/// <see cref="GraphGraftAttribute"/> generates <c>AddTenant</c>, <c>AddTenants</c>, <c>AddUser</c>, <c>AddUsers</c>,
/// <c>AddAgent</c>, and <c>AddAgents</c> in <c>BlueprintsGrafts</c>. Child methods compile only after their parent type.
/// Each factory passes the input <c>Id</c> to the creation model. The input <c>Id</c> is null by default, so the entity
/// makes the ID. Set it to give a fixed ID. The domain factory rejects an ID with the wrong prefix.
/// </remarks>
[GraphGraft]
public static class Blueprints
{
    private static readonly Faker Faker = new();

    public static readonly InputBlueprint<TestSeed, TenantInput, Tenant> Tenant = Blueprint
        .FromInput<TestSeed, TenantInput, Tenant>(
            () => new(),
            (input, _) => Server.Domain.Tenants.Tenant.Create(new TenantForCreation
            {
                Id = input.Id,
                Name = input.Name!,
            }))
        .Derive(i => i.Name, (_, _) => TenantRules.Name(Faker));

    /// <summary>A user is a child of a tenant. The tenant id comes from the parent, never from the test.</summary>
    public static readonly ChildInputBlueprint<TestSeed, Tenant, UserInput, User> User = Blueprint
        .ChildFromInput<TestSeed, Tenant, UserInput, User>(
            () => new(),
            (tenant, input, _) => Server.Domain.Users.User.Create(new UserForCreation
            {
                Id = input.Id,
                TenantId = tenant.Id,
                FirstName = input.FirstName!,
                LastName = input.LastName!,
                Identifier = input.Identifier!,
                Email = input.Email!,
                Username = input.Username!,
                Role = input.Role!,
            }))
        .Derive(i => i.FirstName, (_, _) => UserRules.FirstName(Faker))
        .Derive(i => i.LastName, (_, _) => UserRules.LastName(Faker))
        .Derive(i => i.Identifier, (_, _) => UserRules.Identifier(Faker))
        .Derive(i => i.Email, (_, _) => UserRules.Email(Faker))
        .Derive(i => i.Username, (_, _) => UserRules.Username(Faker))
        .Derive(i => i.Role, (_, _) => UserRules.Role(Faker));

    /// <summary>
    /// An agent is a child of a tenant. The tenant id comes from the parent, never from the test. An operator grants
    /// it, so a test can arrange a machine agent and any grant.
    /// </summary>
    public static readonly ChildInputBlueprint<TestSeed, Tenant, AgentInput, Agent> Agent = Blueprint
        .ChildFromInput<TestSeed, Tenant, AgentInput, Agent>(
            () => new(),
            (tenant, input, _) => Server.Domain.Agents.Agent.Create(new AgentForCreation
            {
                Id = input.Id,
                TenantId = tenant.Id,
                Name = input.Name!,
                ClientId = input.ClientId!,
                Mode = input.Mode!,
                Permissions = input.Permissions!,
            }, Caller.AllTenantsManager.Instance))
        .Derive(i => i.Name, (_, _) => AgentRules.Name(Faker))
        .Derive(i => i.ClientId, (_, _) => AgentRules.ClientId(Faker))
        .Derive(i => i.Mode, (_, _) => AgentRules.Mode(Faker))
        .Derive(i => i.Permissions, (_, _) => []);

    public static readonly ChildInputBlueprint<TestSeed, User, SessionInput, Session> Session = Blueprint
        .ChildFromInput<TestSeed, User, SessionInput, Session>(
            () => new(),
            (user, input, _) => Server.Domain.Sessions.Session.Create(
                user.TenantId, user.Id, input.ClientSessionId!, input.StartedAt!.Value, null, TimeZoneId.Utc, DateTimeOffset.UtcNow))
        .Derive(i => i.ClientSessionId, (_, _) => Faker.Random.Guid().ToString())
        .Derive(i => i.StartedAt, (_, _) => DateTimeOffset.UtcNow.AddMinutes(-Faker.Random.Int(1, 10_000)));

    public static readonly ChildInputBlueprint<TestSeed, User, PersonInput, Server.Domain.People.Person> Person = Blueprint
        .ChildFromInput<TestSeed, User, PersonInput, Server.Domain.People.Person>(
            () => new(),
            (user, input, _) => Server.Domain.People.Person.Create(user.TenantId, user.Id, input.Label!, input.FullName, input.Notes))
        .Derive(i => i.Label, (_, _) => Faker.Person.FullName);

    public static readonly ChildInputBlueprint<TestSeed, User, CorrectionInput, Correction> Correction = Blueprint
        .ChildFromInput<TestSeed, User, CorrectionInput, Correction>(
            () => new(),
            (user, input, _) => Server.Domain.Corrections.Correction.Create(user.TenantId, user.Id, input.CanonicalTerm!, input.Mishearings!, false))
        .Derive(i => i.CanonicalTerm, (_, _) => Faker.Lorem.Word())
        .Derive(i => i.Mishearings, (_, _) => []);
}
