namespace Recall.Server.Domain.Support.Features;

using Exceptions;
using MediatR;
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
        IOwnerGuard ownerGuard,
        IConfiguration configuration,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        TracerProvider tracerProvider,
        MeterProvider meterProvider) : IRequestHandler<Query, SupportBundle>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<SupportBundle> Handle(Query request, CancellationToken cancellationToken)
        {
            var ownerId = await ownerGuard.CallerIfOwnerAsync(cancellationToken)
                ?? throw new ForbiddenAccessException(OnlyTheOwner);

            await FlushPendingTelemetryAsync(cancellationToken);

            _logger.Here().Information("The Owner {UserId} downloaded a support bundle", ownerId);
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
    }
}
