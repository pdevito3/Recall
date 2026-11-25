namespace Recall.Server.Resources.Entrypoints;

using Auth;
using Domain.ImportBatches.Features;
using Domain.Users;
using Http;
using Http.OpenApi;
using Jobs;
using Mcp;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Serilog.Core;
using Spa;

public static class EntrypointRegistration
{
    /// <summary>The path of the MCP streamable HTTP endpoint.</summary>
    public const string McpPath = "/mcp";
    public const string McpTooLargeMessage = "This call is larger than 16 MB. Send less data in each call, for example fewer entries.";

    public static void AddEntrypoints(this WebApplicationBuilder builder)
    {
        builder.Services.AddRecallAuthentication(builder.Configuration, builder.Environment);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSpaCompression();
        builder.Services.AddScoped<ICurrentPrincipalAccessor, HttpContextPrincipalAccessor>();

        // HTTP
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = DomainProblemDetails.Customize);
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, EntrypointJsonContext.Default));
        builder.Services.AddApiVersioningExtension();
        // A value that does not bind throws, so that DomainProblemDetails gives a 400 with the errors, not an empty 400
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.AddSwaggerExtension(builder.Configuration);

        // The job dashboard checks an antiforgery token on each write.
        builder.Services.AddAntiforgery();
        builder.Services.AddExceptionHandler<JobDashboardFaultPage>();

        // MCP. The transport is stateless, so each tool call runs in its own HTTP request. Then the
        // HttpContext, the caller, and the tenant scope are the ones of that request.
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly(typeof(EntrypointRegistration).Assembly)
            .WithRequestFilters(filters =>
            {
                filters.AddListToolsFilter(McpToolAccess.ListFilter);
                // The first call filter is the outermost, so a denial of the access filter becomes a tool error.
                filters.AddCallToolFilter(McpCallTelemetry.Filter);
                filters.AddCallToolFilter(McpDomainErrors.Filter);
                filters.AddCallToolFilter(McpToolAccess.CallFilter);
            });
        // ReadFrom.Services in HostExtensions adds each ILogEventFilter of the services to the logger
        builder.Services.AddSingleton<ILogEventFilter, McpProtocolErrorLogFilter>();
    }

    public static void UseEntrypoints(this WebApplication app)
    {
        app.UseJobDashboardAnswerHeaders();
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            StatusCodeSelector = DomainProblemDetails.StatusCodeFor,
            // A client error is an expected outcome, so it is not logged as an unhandled exception.
            SuppressDiagnosticsCallback = context => DomainProblemDetails.IsClientError(context.Exception),
        });
        app.UseRequestTooLargeProblem();
        app.UseSpaStaticFiles();
        app.UseNoStoreAnswers();
        // Routing checks the request content type, so the text/json rewrite must run before routing
        app.UseMiddleware<JsonMediaTypeMiddleware>();
        app.UseRouting();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.UseSwaggerExtension(app.Configuration);
        }

        app.UseSpaFallback();
        app.UseApiNotFound();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UsePageUserCheck();
        app.UseAuthorization();
    }

    public static void MapEntrypoints(this WebApplication app)
    {
        app.MapVersionedEndpoints();
        McpToolAccess.Validate(app.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection ?? []);
        app.MapMcp(McpPath).RequireAuthorization().WithMetadata(
            new RequestSizeLimitAttribute(PutImportEntriesEndpoint.MaxBodyBytes),
            new RequestTooLargeDetail(McpTooLargeMessage));
        app.MapJobMcp();
        if (AuthRegistration.JwtIsOn(app.Configuration))
            app.MapMcpResourceMetadata();
        app.MapRecallAuth();
    }
}
