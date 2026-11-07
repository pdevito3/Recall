namespace Recall.Server.Domain.ModelProviders.Features;

using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Users;

public static class GetModelProviderList
{
    public sealed record Query : IRequest<ModelProviderListDto>;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissions, ApiKeyProtector keys, KeyRingStatus keyRing)
        : IRequestHandler<Query, ModelProviderListDto>
    {
        public async Task<ModelProviderListDto> Handle(Query request, CancellationToken cancellationToken)
        {
            await permissions.RequireAsync(Permission.ManageModelProviders(), cancellationToken);

            var providers = await dbContext.ModelProviders.AsNoTracking()
                .TagWithOperationCallSite("model_providers.list")
                .NewestActivationFirst()
                .ToListAsync(cancellationToken);

            var active = providers.FirstOrDefault(provider => provider is { ActivatedOn: not null, Model: not null });
            var items = providers.Select(provider => provider.ToDto(keys, isActive: provider == active)).ToList();
            return new ModelProviderListDto
            {
                Items = items,
                KeyRing = KeyRingStatus.StateFor(keyRing.FolderIsWritable, items.Any(i => i.KeyState == nameof(ModelProviderKeyState.Unreadable))).ToString(),
            };
        }
    }
}
