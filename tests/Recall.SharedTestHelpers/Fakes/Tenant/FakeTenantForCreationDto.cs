namespace Recall.SharedTestHelpers.Fakes.Tenant;

using AutoBogus;
using Recall.Server.Domain.Tenants.Dtos;

public sealed class FakeTenantForCreationDto : AutoFaker<TenantForCreationDto>
{
    public FakeTenantForCreationDto()
    {
        RuleFor(x => x.Name, TenantRules.Name);
    }
}
