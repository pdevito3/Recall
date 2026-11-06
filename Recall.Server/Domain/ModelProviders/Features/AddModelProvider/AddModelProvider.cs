namespace Recall.Server.Domain.ModelProviders.Features;

using System.Diagnostics;
using Databases;
using Dtos;
using Exceptions;
using Mappings;
using MediatR;
using Resources;
using Serilog;
using Users;

public static class AddModelProvider
{
    public sealed record Command(ModelProviderForCreationDto Dto) : IRequest<ModelProviderDto>;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissions, ApiKeyProtector keys, TimeProvider timeProvider)
        : IRequestHandler<Command, ModelProviderDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<ModelProviderDto> Handle(Command request, CancellationToken cancellationToken)
        {
            await permissions.RequireAsync(Permission.ManageModelProviders(), cancellationToken);
            var tenantId = dbContext.CurrentTenantScope.TenantId ?? throw new ForbiddenAccessException("A model provider belongs to one tenant.");
            var protectedKey = keys.Protect(ModelProvider.ApiKeyOf(request.Dto.ApiKey));

            var provider = await RootLock.Tenant.RunSharedAsync(dbContext, tenantId, async ct =>
            {
                var connected = ModelProvider.Connect(tenantId, ModelProvider.KindOf(request.Dto.Kind), request.Dto.Name, request.Dto.BaseUrl, protectedKey, timeProvider.GetUtcNow());
                await dbContext.ModelProviders.AddAsync(connected, ct);
                await dbContext.SaveChangesAsync(ct);
                return connected;
            }, cancellationToken);

            Activity.Current?.SetTag("app.model_provider_id", provider.Id);
            _logger.Here().Information("Connected model provider {ModelProviderId} of kind {ModelProviderKind}", provider.Id, provider.Kind);
            return provider.ToDto(keys, isActive: true);
        }
    }
}
