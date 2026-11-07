namespace Recall.Server.Domain.ModelProviders.Features;

using System.Diagnostics;
using Databases;
using Databases.Sqlite;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;
using Users;

public static class DeleteModelProvider
{
    public sealed record Command(string Id) : IRequest;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissions) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            await permissions.RequireAsync(Permission.ManageModelProviders(), cancellationToken);
            Activity.Current?.SetTag("app.model_provider_id", request.Id);

            var provider = await dbContext.ModelProviders
                .TagWithOperationCallSite("model_providers.delete.load")
                .GetById(request.Id, cancellationToken);
            provider.ForgetKey();
            dbContext.ModelProviders.Remove(provider);
            await dbContext.SaveChangesAsync(cancellationToken);

            var scrubbed = await WalScrub.TryTruncateAsync(dbContext.Database.GetConnectionString()!);
            _logger.Here().Information("Deleted model provider {ModelProviderId}, WAL scrubbed {WalScrubbed}", provider.Id, scrubbed);
        }
    }
}
