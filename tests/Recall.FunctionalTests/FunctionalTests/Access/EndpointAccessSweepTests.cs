namespace Recall.FunctionalTests.FunctionalTests.Access;

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recall.Server.Domain;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints;
using Recall.Server.Resources.Entrypoints.Auth;
using Recall.Server.Resources.Entrypoints.Http;
using Recall.Server.Resources.Entrypoints.Mcp;
using Recall.SharedTestHelpers.EntityGraphs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenApi;
using TestUtilities;
using TestUtilities.AccessSweep;

public class EndpointAccessSweepTests : TestBase
{
    // An entry that matches no entrypoint does no harm, so that the removal of a feature does not break the sweep
    private static readonly IReadOnlyDictionary<string, Open> OpenToEachMember = new Dictionary<string, Open>
    {
        ["GetTenant"] = new("Each member reads the own tenant. The tenant filter hides each other tenant."),
        ["GetTenantList"] = new("Each member lists the own tenant. The tenant filter hides each other tenant."),
        ["GetCurrentUser"] = new("Each signed-in caller reads the own identity. It takes no ID, and its one read is the own user row behind the tenant filter."),
        ["get_tenant"] = new("ToolAccessKind.Read. Each member reads the own tenant, and the tenant filter hides the others."),
        ["list_tenants"] = new("ToolAccessKind.Read. Each member lists the own tenant, and the tenant filter hides the others."),
        // new-feature:open-to-each-member
    };

    private static readonly Dictionary<string, string> InfrastructureRoutes = new()
    {
        ["/health"] = "The readiness probe of the host. It shows no tenant data.",
        ["/alive"] = "The liveness probe of the host. It shows no tenant data.",
        [$"{EntrypointRegistration.McpPath}/"] = "The MCP transport. The MCP test sweeps each tool behind it.",
        [McpResourceMetadata.Path] = "The public OAuth metadata of the MCP endpoint (RFC 9728). It shows no tenant data.",
        [AuthRoutes.Status] = "Public. A phone reads it before sign-in. It names the sign-in methods and shows no tenant data.",
        [AuthRoutes.Login] = "Public. The chooser page and its redirects. It shows no tenant data.",
        [AuthRoutes.Start] = "Public and rate limited. It starts a login and shows no tenant data.",
        [AuthRoutes.Callback] = "The binding cookie and the state prove this request. AuthRefusalTests covers each refusal.",
        [AuthRoutes.Confirm] = "The one-time code in the form proves this request. It writes nothing and shows no tenant data.",
        [AuthRoutes.Token] = "The code and PKCE verifier, or the refresh token, prove this request. AuthRefusalTests covers each refusal.",
        [AuthRoutes.Logout] = "Tolerant. It ends only a session that the request proves, and it shows no tenant data.",
        [AuthRoutes.SignedOut] = "Public. It redirects to the signed-out page, takes no value, and shows no tenant data.",
        [AuthRoutes.RevokeOthers] = "Each session ends only the other sessions of its own user. AppSessionTests covers it.",
        [AuthRoutes.Me] = "Each session reads its own identity, and a visitor with no session gets 204. It takes no ID. AppSessionTests covers it.",
    };

    // The sample body of an update sends version 0, so the control call of an update gets the stale-version code
    private static readonly SurfaceCodes HttpCodes = new("403", "404", "409", "401", code => code[0] == '2');
    // /mcp refuses a call with no token before a tool runs. TenantMcpToolTests checks that 401.
    private static readonly SurfaceCodes McpCodes = new("permission_denied", "not_found", "aborted", null, code => code == "ok");

    private static Assembly ServerAssembly => typeof(EndpointDiscovery).Assembly;

    private static IServiceProvider Services => TestingWebApplicationFactory.Instance.Services;

    [Test]
    public async Task each_http_endpoint_denies_a_member_with_no_permission_and_hides_the_records_of_other_tenants()
    {
        // Arrange
        var scenario = await SweepScenario.AddAsync();
        var document = await OpenApiDocumentTests.LoadDocumentAsync();
        var routes = Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToList();
        var versioned = routes
            .Where(e => e.RoutePattern.RawText?.StartsWith(EndpointDiscovery.VersionedRoutePrefix, StringComparison.Ordinal) == true)
            .ToList();
        var endpoints = versioned
            .Select(e => HttpEntrypoint.From(e, document))
            // A delete runs last, so that a gap in it cannot remove a record that a later call needs
            .OrderBy(e => e.IsDelete)
            .ThenBy(e => e.Method, StringComparer.Ordinal)
            .ThenBy(e => e.Target.Key, StringComparer.Ordinal)
            .ToList();
        var unsweptRoutes = routes.Except(versioned)
            .Where(e => !InfrastructureRoutes.ContainsKey(e.RoutePattern.RawText ?? ""))
            .Select(e => $"{e.DisplayName} ({e.RoutePattern.RawText}): it is outside the versioned group "
                + $"{EndpointDiscovery.VersionedRoutePrefix}, so the sweep does not call it. Map it as an {nameof(IEndpoint)}, "
                + $"or add it to {nameof(InfrastructureRoutes)} with the reason.");
        // Reflection, a test: it reads the type of each handler. The handler of a lambda is on a closure type inside
        // the endpoint type.
        var mappedEndpointTypes = versioned
            .SelectMany(e => TypeAndOuterTypes(e.Metadata.OfType<MethodInfo>().FirstOrDefault()?.DeclaringType))
            .ToHashSet();
        var unmappedEndpointTypes = EndpointDiscovery.EndpointTypes(ServerAssembly)
            .Where(type => !mappedEndpointTypes.Contains(type))
            .Select(type => $"{type.FullName}: it is an {nameof(IEndpoint)}, but the sweep found no versioned endpoint "
                + "with its handler, so the discovery missed it.");

        // Act
        var report = await SweepAsync(scenario, endpoints, e => e.Target, HttpCodes, SendHttpAsync,
            [.. unsweptRoutes, .. unmappedEndpointTypes]);

        // Assert
        report.WriteCoverageTable();
        report.ShouldHaveNoGaps();
    }

    [Test]
    public async Task each_mcp_tool_denies_a_member_with_no_permission_and_hides_the_records_of_other_tenants()
    {
        // Arrange
        var scenario = await SweepScenario.AddAsync();
        var tools = Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection ?? [];
        var entrypoints = tools.Select(McpEntrypoint.From).OrderBy(t => t.Target.Key, StringComparer.Ordinal).ToList();
        var registeredMethods = entrypoints.Select(t => t.MethodName).ToHashSet();
        // Reflection, a test: it finds each method with [McpServerTool] in the Server
        var unregisteredTools = ServerAssembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(McpEntrypoint.NameOfMethod)
            .Where(name => !registeredMethods.Contains(name))
            .Select(name => $"{name}: it has [McpServerTool], but the MCP server has no tool for it, so the sweep did not call it.");
        FactoryClient.SignInAsAppUser(scenario.Own.Member.Identifier);
        await using var client = await CreateMcpClientAsync();

        // Act
        var report = await SweepAsync(scenario, entrypoints, t => t.Target, McpCodes, async (tool, ids) =>
        {
            try
            {
                var result = await client.CallToolAsync(tool.Target.Key, tool.ArgumentsFor(ids));
                if (result.IsError != true)
                    return new Outcome("ok", "");
                var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
                var code = result.StructuredContent is { ValueKind: JsonValueKind.Object } content
                    && content.TryGetProperty("code", out var property) ? property.GetString() : null;
                return new Outcome(code ?? "tool error", text);
            }
            catch (McpException exception)
            {
                return new Outcome("protocol error", exception.Message);
            }
        }, [.. unregisteredTools]);

        // Assert
        report.WriteCoverageTable();
        report.ShouldHaveNoGaps();
    }

    // Reflection, a test: it walks the outer types of a nested type
    private static IEnumerable<Type> TypeAndOuterTypes(Type? type)
    {
        for (; type is not null; type = type.DeclaringType)
            yield return type;
    }

    private async Task<SweepReport> SweepAsync<T>(
        SweepScenario scenario,
        IReadOnlyList<T> entrypoints,
        Func<T, SweepTarget> targetOf,
        SurfaceCodes codes,
        Func<T, RecordIds, Task<Outcome>> send,
        IReadOnlyList<string> discoveryGaps)
    {
        var report = new SweepReport();
        report.Gaps.AddRange(discoveryGaps);
        report.Gaps.AddRange(AccessRules.DuplicateKeyGaps(entrypoints.Select(targetOf)));
        foreach (var entrypoint in entrypoints)
        {
            var target = targetOf(entrypoint);
            var outcomes = new Dictionary<SweepCall, Outcome>();
            try
            {
                foreach (var call in AccessRules.CallsFor(codes, target))
                {
                    var (caller, ids) = await scenario.PartiesOfAsync(call);
                    if (caller is null)
                        FactoryClient.DefaultRequestHeaders.Authorization = null;
                    else
                        FactoryClient.SignInAsAppUser(caller.Identifier);
                    outcomes[call] = await send(entrypoint, ids);
                }
            }
            catch (SweepSetupException exception)
            {
                report.Gaps.Add($"{AccessRules.NameOf(target)}: {exception.Message}");
                continue;
            }

            report.Rows.Add($"{AccessRules.NameOf(target)}: " + string.Join(", ", outcomes.Select(o => $"{o.Key}={o.Value.Code}")));
            report.Gaps.AddRange(AccessRules.Gaps(codes, target, outcomes, OpenToEachMember));
        }
        return report;
    }

    private sealed class SweepReport
    {
        public List<string> Rows { get; } = [];
        public List<string> Gaps { get; } = [];

        public void WriteCoverageTable()
            => Console.WriteLine($"Swept {Rows.Count} entrypoints:{Environment.NewLine}{string.Join(Environment.NewLine, Rows)}");

        public void ShouldHaveNoGaps()
        {
            if (Gaps.Count > 0)
                throw new InvalidOperationException(
                    $"The sweep found {Gaps.Count} access {(Gaps.Count == 1 ? "gap" : "gaps")}:{Environment.NewLine}{string.Join(Environment.NewLine, Gaps)}");
        }
    }

    private sealed record TenantRecords(User Member, User Admin, RecordIds Ids);

    private sealed record SweepScenario(TenantRecords Own, TenantRecords Other, RecordIds Unknown)
    {
        public static async Task<SweepScenario> AddAsync()
        {
            var own = await AddTenantAsync();
            var other = await AddTenantAsync();
            return new SweepScenario(own, other, own.Ids.Unknown());
        }

        public async Task<(User? Caller, RecordIds Ids)> PartiesOfAsync(SweepCall call)
        {
            switch (call)
            {
                case SweepCall.NoUser:
                    return (null, Unknown);
                case SweepCall.MemberWithOwnTenantIds:
                    return (Own.Member, Own.Ids);
                case SweepCall.MemberWithUnknownIds:
                    return (Own.Member, Unknown);
                case SweepCall.AdminWithOtherTenantIds:
                    return (Own.Admin, Other.Ids);
                case SweepCall.AdminWithUnknownIds:
                    return (Own.Admin, Unknown);
                case SweepCall.AdminWithOwnTenantIds:
                    // The control call can change or delete its records, so it gets a tenant of its own
                    var control = await AddTenantAsync();
                    return (control.Admin, control.Ids);
                default:
                    throw new ArgumentOutOfRangeException(nameof(call), call, null);
            }
        }
    }

    private sealed record RecordIds(IReadOnlyDictionary<string, string> IdsByKind, string Identifier)
    {
        private static readonly string PermissionName = Permission.ListNames()[0];

        public RecordIds Unknown() => new(
            IdsByKind.ToDictionary(kind => kind.Key, kind => EntityId.New(kind.Value[..kind.Value.IndexOf('_')])),
            $"unknown-{Guid.NewGuid():N}");

        // A route value that names a record under the record of {id}. Its value is under "<kind>/<name>" in IdsByKind.
        private static readonly HashSet<string> ChildRecordNames =
        [
        ];

        public static bool NamesARecord(string name) => name is "id" or "identifier" || ChildRecordNames.Contains(name);

        public string? ValueFor(string name, string kind) => name switch
        {
            "id" => IdsByKind.TryGetValue(kind, out var id)
                ? id
                : throw new SweepSetupException($"It has an id of the kind '{kind}', which the sweep does not know. "
                    + $"Make a record of this kind in {nameof(AddTenantAsync)} and add its ID under '{kind}'."),
            "identifier" => Identifier,
            _ when ChildRecordNames.Contains(name) => IdsByKind.TryGetValue($"{kind}/{name}", out var childId)
                ? childId
                : throw new SweepSetupException($"It has the route value {{{name}}} under '{kind}', which the sweep does not know. "
                    + $"Make a record of this kind in {nameof(AddTenantAsync)} and add its ID under '{kind}/{name}'."),
            "permission" => PermissionName,
            _ => null,
        };
    }

    private static async Task<TenantRecords> AddTenantAsync()
    {
        TestGraph.Start(out _)
            .AddTenant(out var tenant)
            .AddUsers(out var users, u => u.Role = "User", u => u.Role = "Admin", u => u.Role = "User")
            .Under(tenant)
            // new-feature:sweep-graph
            .AddAgent(out var agent);
        var (member, admin, otherUser) = (users[0], users[1], users[2]);
        foreach (var permission in member.UserPermissions.ToList())
            member.RemovePermission(permission);
        admin.AddPermissions(Permission.GetAll());

        await InsertAsync(tenant);
        await InsertAsync(users);
        await InsertAsync(agent);
        // new-feature:sweep-inserts

        var idsByKind = new Dictionary<string, string>
        {
            ["users"] = otherUser.Id,
            ["agents"] = agent.Id,
            ["tenants"] = tenant.Id,
            // new-feature:sweep-ids
        };
        return new TenantRecords(member, admin, new RecordIds(idsByKind, otherUser.Identifier));
    }

    // ...Domain.AuditEntries.Features gives audit-entries, and ...Domain.Tenants.Features gives tenants
    private static string KindOfNamespace(string? ns)
    {
        var segments = (ns ?? "").Split('.');
        var root = Array.FindIndex(segments, s => s is "Domain" or "Examples");
        if (root < 0 || root + 1 >= segments.Length)
            return $"{ns} (no Domain or Examples folder)";
        var kind = new StringBuilder();
        foreach (var c in segments[root + 1])
        {
            if (char.IsUpper(c) && kind.Length > 0)
                kind.Append('-');
            kind.Append(char.ToLowerInvariant(c));
        }
        return kind.ToString();
    }

    private async Task<Outcome> SendHttpAsync(HttpEntrypoint endpoint, RecordIds ids)
    {
        using var request = endpoint.RequestFor(ids);
        using var response = await FactoryClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return new Outcome(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), body.Length > 300 ? body[..300] : body);
    }

    private sealed record HttpParameter(string Name, string In, bool Required, JsonNode? Schema);

    private sealed record HttpEntrypoint(
        SweepTarget Target,
        string Method,
        string Path,
        string Kind,
        IReadOnlyList<HttpParameter> Parameters,
        JsonNode? Body,
        JsonNode Document)
    {
        public bool IsDelete => Method == HttpMethods.Delete;

        public static HttpEntrypoint From(RouteEndpoint endpoint, JsonNode document)
        {
            var route = endpoint.RoutePattern.RawText![EndpointDiscovery.VersionedRoutePrefix.Length..];
            var method = string.Join(",", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [HttpMethods.Get]);
            var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? $"{method} {route}";
            var target = new SweepTarget(name, $"{method} /{EndpointDiscovery.VersionedRoutePrefix}{route}",
                endpoint.RoutePattern.Parameters.Any(p => RecordIds.NamesARecord(p.Name)));
            var kind = route.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

            var operation = OpenApiDocumentTests.Operations(document)
                .Select(o => (Path: o.Name[(o.Method.Length + 1)..], o.Operation))
                .FirstOrDefault(o => o.Operation["operationId"]?.GetValue<string>() == name);
            if (operation.Operation is null)
                return new HttpEntrypoint(target, method, "", kind, [], null, document);

            var parameters = (operation.Operation["parameters"]?.AsArray() ?? [])
                .Select(p => new HttpParameter(
                    p!["name"]!.GetValue<string>(),
                    p["in"]!.GetValue<string>(),
                    p["required"]?.GetValue<bool>() == true,
                    p["schema"]))
                .ToList();
            var content = operation.Operation["requestBody"]?["content"];
            return new HttpEntrypoint(target with { Label = $"{method} {operation.Path}" },
                method, operation.Path, kind, parameters, content?["application/json"]?["schema"], document);
        }

        /// <param name="ids">The records that the route values name.</param>
        public HttpRequestMessage RequestFor(RecordIds ids)
        {
            if (Method.Contains(',', StringComparison.Ordinal))
                throw new SweepSetupException($"It maps the methods {Method}, and the sweep sends one method. Map one method on each endpoint.");
            if (Path.Length == 0)
                throw new SweepSetupException($"The v1 OpenAPI document has no operation named {Target.Key}.");

            var path = Path;
            foreach (var parameter in Parameters.Where(p => p.In == "path"))
            {
                var value = ids.ValueFor(parameter.Name, Kind)
                    ?? throw new SweepSetupException($"The sweep does not know the route value {{{parameter.Name}}}. "
                        + $"Give it the value of a real record in {nameof(RecordIds)}.{nameof(RecordIds.ValueFor)}. If the value "
                        + $"names a record of a tenant, as a slug can, also add it to {nameof(RecordIds)}.{nameof(RecordIds.NamesARecord)} "
                        + $"and give {nameof(RecordIds)}.{nameof(RecordIds.Unknown)} a value that names no record.");
                path = path.Replace($"{{{parameter.Name}}}", Uri.EscapeDataString(value), StringComparison.Ordinal);
            }

            var query = Parameters
                .Where(p => p is { In: "query", Required: true })
                .Select(p => $"{p.Name}={Uri.EscapeDataString(QueryValueOf(p))}")
                .ToList();
            var request = new HttpRequestMessage(new HttpMethod(Method),
                path.TrimStart('/') + (query.Count > 0 ? $"?{string.Join('&', query)}" : ""));
            if (Body is not null)
                request.Content = new StringContent(
                    SchemaSample.Of(Body, Document)?.ToJsonString() ?? "null", Encoding.UTF8, "application/json");
            return request;
        }

        private string QueryValueOf(HttpParameter parameter)
            => SchemaSample.Of(parameter.Schema ?? new JsonObject(), Document) is JsonValue value
                ? value.ToString()
                : throw new SweepSetupException($"The sweep sends only a plain value for the query value {parameter.Name}.");
    }

    private sealed record McpEntrypoint(SweepTarget Target, JsonNode InputSchema, string Kind, string MethodName)
    {
        public static McpEntrypoint From(McpServerTool tool)
        {
            var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!;
            var properties = schema["properties"]?.AsObject().Select(p => p.Key) ?? [];
            // Reflection, a test: it reads the method of the tool and its namespace. The SDK puts the method first in
            // its metadata.
            var method = tool.Metadata.OfType<MethodInfo>().First();
            var target = new SweepTarget(tool.ProtocolTool.Name, $"MCP tool {tool.ProtocolTool.Name}",
                properties.Any(RecordIds.NamesARecord));
            return new McpEntrypoint(target, schema, KindOfNamespace(method.DeclaringType?.Namespace), NameOfMethod(method));
        }

        // Reflection, a test: it names a tool method by its type
        public static string NameOfMethod(MethodInfo method) => $"{method.DeclaringType?.FullName}.{method.Name}";

        public Dictionary<string, object?> ArgumentsFor(RecordIds ids)
            => SchemaSample.RequiredOf(InputSchema, InputSchema).ToDictionary(
                name => name,
                name => ids.ValueFor(name, Kind) is { } value
                    ? JsonSerializer.SerializeToElement(value)
                    : (object?)JsonSerializer.SerializeToElement(
                        SchemaSample.Of(InputSchema["properties"]![name]!, InputSchema)));
    }
}
