namespace Recall.Server.Domain.ModelProviders.Features;

using System.Diagnostics;
using Clients;
using Databases;
using Dtos;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;
using Users;

public static class TestModelProvider
{
    public const string UnreadableKeyMessage = "The key ring of this server cannot read the key. Enter the API key again.";

    public sealed record Command(string Id) : IRequest<ModelProviderTestResultDto>;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissions, ApiKeyProtector keys, IModelProviderClient client)
        : IRequestHandler<Command, ModelProviderTestResultDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<ModelProviderTestResultDto> Handle(Command request, CancellationToken cancellationToken)
        {
            await permissions.RequireAsync(Permission.ManageModelProviders(), cancellationToken);
            Activity.Current?.SetTag("app.model_provider_id", request.Id);

            var provider = await dbContext.ModelProviders.AsNoTracking()
                .TagWithOperationCallSite("model_providers.test.load")
                .GetById(request.Id, cancellationToken);

            var read = keys.Read(provider.ProtectedApiKey);
            if (read is ProtectedKeyRead.Unreadable)
                return FailureOf(new ProviderFailure(ProviderFailureKind.Refused, null, UnreadableKeyMessage));

            var endpoint = new ModelEndpoint(provider.Kind, provider.BaseUrl, (read as ProtectedKeyRead.Readable)?.ApiKey);
            var reply = await client.ListModelsAsync(endpoint, cancellationToken);

            // The failure holds the words of the provider, so the line names only its kind and its status.
            _logger.Here().Information("Tested model provider {ModelProviderId}, reply {ModelProviderReply}", provider.Id, reply);
            return reply switch
            {
                ProviderReply<IReadOnlyList<ProviderModel>>.Answered answered => new ModelProviderTestResultDto
                {
                    Ok = true,
                    Models = answered.Value.Select(m => new ProviderModelDto { Id = m.Id, DisplayName = m.DisplayName }).ToList(),
                },
                ProviderReply<IReadOnlyList<ProviderModel>>.Failed failed => FailureOf(failed.Failure),
                _ => throw new InvalidOperationException(),
            };
        }

        private static ModelProviderTestResultDto FailureOf(ProviderFailure failure) => new()
        {
            Failure = new ModelProviderFailureDto { Kind = failure.Kind.ToString(), StatusCode = failure.StatusCode, Message = failure.DisplayMessage },
        };
    }
}
