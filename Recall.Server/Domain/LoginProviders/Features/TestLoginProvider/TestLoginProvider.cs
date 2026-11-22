namespace Recall.Server.Domain.LoginProviders.Features;

using System.Text.Json;
using Auth;
using Dtos;
using MediatR;
using Microsoft.Extensions.Options;
using Resources;
using Serilog;
using Users;

public static class TestLoginProvider
{
    public const string UnreachableMessage = "The server cannot reach this issuer.";
    public const string NotDiscoveryMessage = "This URL gives no OpenID Connect discovery document.";
    public const string NoS256Message = "This provider does not offer S256 PKCE, which Recall needs.";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);
    private const int AnswerLimitBytes = 1024 * 1024;

    public static string OtherIssuerMessage(string issuer) => $"The provider names another issuer: {issuer}.";

    public sealed record Command(LoginProviderForTestDto Dto) : IRequest<LoginProviderTestResultDto>;

    /// <summary>Runs discovery with the CA and the <c>allow_http</c> of the form, and saves nothing (IDP-7).</summary>
    public sealed class Handler(IPermissionGuard permissionGuard, OidcHandlerFactory handlers, IOptions<AuthOptions> options) : IRequestHandler<Command, LoginProviderTestResultDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<LoginProviderTestResultDto> Handle(Command request, CancellationToken cancellationToken)
        {
            var caller = await permissionGuard.RequireSignedInPersonAsync(cancellationToken);
            caller.Require(Permission.ManageUserAccess());

            var issuer = LoginProvider.IssuerOf(request.Dto.Issuer, request.Dto.AllowHttp);
            var settings = new OidcProviderOptions
            {
                Issuer = issuer,
                AllowHttp = request.Dto.AllowHttp,
                CaCertificatePath = LoginProvider.CaCertificatePathOf(request.Dto.CaCertificatePath),
                RefusesLoopback = !options.Value.AllowLoopbackProviders,
            };

            var result = await DiscoverAsync(settings, cancellationToken);
            _logger.Here().Information("Tested a login provider, failure {LoginProviderTestFailure}", result.Failure?.Kind ?? "none");
            return result;
        }

        private async Task<LoginProviderTestResultDto> DiscoverAsync(OidcProviderOptions settings, CancellationToken cancellationToken)
        {
            using var http = new HttpClient(handlers(settings)) { Timeout = CallTimeout, MaxResponseContentBufferSize = AnswerLimitBytes };
            string body;
            try
            {
                using var response = await http.GetAsync(settings.Issuer!.TrimEnd('/') + "/.well-known/openid-configuration", cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return Failed(LoginProviderTestFailure.NotDiscovery, NotDiscoveryMessage);
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return Failed(LoginProviderTestFailure.Unreachable, UnreachableMessage);
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("issuer", out var named) || named.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("authorization_endpoint", out _) || !root.TryGetProperty("token_endpoint", out _))
                    return Failed(LoginProviderTestFailure.NotDiscovery, NotDiscoveryMessage);

                var discovered = named.GetString()!;
                if (!LoginProvider.SameIssuer(discovered, settings.Issuer))
                    return Failed(LoginProviderTestFailure.OtherIssuer, OtherIssuerMessage(discovered));

                var offersS256 = root.TryGetProperty("code_challenge_methods_supported", out var methods) && methods.ValueKind == JsonValueKind.Array
                    && methods.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.String && m.GetString() == "S256");
                return offersS256
                    ? new LoginProviderTestResultDto { Ok = true, Issuer = discovered }
                    : Failed(LoginProviderTestFailure.NoS256, NoS256Message);
            }
            catch (JsonException)
            {
                return Failed(LoginProviderTestFailure.NotDiscovery, NotDiscoveryMessage);
            }
        }

        private static LoginProviderTestResultDto Failed(LoginProviderTestFailure failure, string message)
            => new() { Failure = new LoginProviderTestFailureDto { Kind = failure.ToString(), Message = message } };
    }
}
