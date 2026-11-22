namespace Recall.FunctionalTests.Auth;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

/// <summary>An IdP on a socket of 127.0.0.1, which serves only its discovery document, for a test of the client of a provider from the app.</summary>
public sealed class LoopbackIdp : IAsyncDisposable
{
    private static readonly string[] S256Only = ["S256"];

    private readonly WebApplication _app;

    private LoopbackIdp()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.Run(AnswerAsync);
    }

    public string Issuer => _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');

    public static async Task<LoopbackIdp> StartAsync()
    {
        var idp = new LoopbackIdp();
        await idp._app.StartAsync();
        return idp;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private Task AnswerAsync(HttpContext context)
    {
        if (context.Request.Path != "/.well-known/openid-configuration")
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["issuer"] = Issuer,
            ["authorization_endpoint"] = $"{Issuer}/authorize",
            ["token_endpoint"] = $"{Issuer}/token",
            ["jwks_uri"] = $"{Issuer}/jwks",
            ["code_challenge_methods_supported"] = S256Only,
        }));
    }
}
