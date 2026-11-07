namespace Recall.Server.Domain.ModelProviders.Features;

using System.Diagnostics;
using AuditEntries;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;
using Users;

public static class UpdateModelProvider
{
    public sealed record Command(string Id, ModelProviderForUpdateDto Dto) : IRequest<ModelProviderDto>;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissions, ApiKeyProtector keys, TimeProvider timeProvider)
        : IRequestHandler<Command, ModelProviderDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<ModelProviderDto> Handle(Command request, CancellationToken cancellationToken)
        {
            await permissions.RequireAsync(Permission.ManageModelProviders(), cancellationToken);
            Activity.Current?.SetTag("app.model_provider_id", request.Id);

            var provider = await dbContext.ModelProviders
                .TagWithOperationCallSite("model_providers.update.load")
                .GetById(request.Id, cancellationToken);

            var protectedKey = keys.Protect(ModelProvider.ApiKeyOf(request.Dto.ApiKey));
            provider.Update(request.Dto.Name, request.Dto.BaseUrl, protectedKey, request.Dto.Model, request.Dto.Version);
            if (protectedKey is not null)
                dbContext.RecordAction(new ModelProviderKeyChanged(AuditSubject.Of(provider)));
            if (request.Dto.MakeActive)
                provider.Activate(timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);

            var active = await dbContext.ModelProviders
                .TagWithOperationCallSite("model_providers.update.active")
                .ActiveFirst()
                .Select(p => p.Id)
                .FirstOrDefaultAsync(cancellationToken);

            _logger.Here().Information("Updated model provider {ModelProviderId}", provider.Id);
            return provider.ToDto(keys, active == provider.Id);
        }
    }
}
