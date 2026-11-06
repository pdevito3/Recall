namespace Recall.SharedTestHelpers.Fakes.Tenant;

using AutoBogus;
using Recall.Server.Domain.Tenants.Models;

public sealed class FakeTenantForCreation : AutoFaker<TenantForCreation>
{
    public FakeTenantForCreation()
    {
        // A null ID makes the domain factory generate a new ID.
        RuleFor(x => x.Id, _ => null);
        RuleFor(x => x.Name, TenantRules.Name);
    }
}
