namespace Recall.Server.Domain.Support.Features;

using Databases;
using Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Resources;
using Resources.FileTelemetry;
using Serilog;
using Users;

public static class GetSupportBundle
{
    public const string OnlyTheOwner = "Only the Owner of this install can download the support bundle.";

    private const int FlushMilliseconds = 5000;

    public sealed record Query : IRequest<SupportBundle>;

    public sealed class Handler(
        IPermissionGuard permissionGuard,
        AppDbContext dbContext,
        IConfiguration configuration,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        TracerProvider tracerProvider,
        MeterProvider meterProvider) : IRequestHandler<Query, SupportBundle>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<SupportBundle> Handle(Query request, CancellationToken cancellationToken)
        {
            var caller = await permissionGuard.GetCallerAsync(cancellationToken);
            if (caller is not Caller.Member member || member.UserId != await OwnerIdAsync(cancellationToken))
                throw new ForbiddenAccessException(OnlyTheOwner);

            await FlushPendingTelemetryAsync(cancellationToken);

            _logger.Here().Information("The Owner {UserId} downloaded a support bundle", member.UserId);
            return new SupportBundle(
                FileTelemetryPlan.For(configuration),
                configuration.AsEnumerable(),
                environment.EnvironmentName,
                timeProvider.GetUtcNow());
        }

        private Task FlushPendingTelemetryAsync(CancellationToken cancellationToken) => Task.Run(() =>
        {
            tracerProvider.ForceFlush(FlushMilliseconds);
            meterProvider.ForceFlush(FlushMilliseconds);
        }, cancellationToken);

        private async Task<string?> OwnerIdAsync(CancellationToken cancellationToken)
        {
            var oldestTenantId = await dbContext.Tenants
                .TagWithOperationCallSite("support.bundle.oldest_tenant")
                .IgnoreQueryFilters()
                .Where(t => !t.IsDeleted)
                .OrderBy(t => t.Id)
                .Select(t => t.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (oldestTenantId is null)
                return null;

            return await dbContext.Users
                .TagWithOperationCallSite("support.bundle.owner")
                .OwnerFirstIn(oldestTenantId)
                .Select(u => u.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
    }
}
