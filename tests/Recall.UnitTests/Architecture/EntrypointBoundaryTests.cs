namespace Recall.UnitTests.Architecture;

using Recall.ArchitectureFixtures;
using Shouldly;

/// <summary>
/// The Server is one project, and an adapter lives in the folder of its feature, next to the core. So no project
/// reference and no folder keeps the transports out of the core. <see cref="BoundaryScanner"/> enforces these rules
/// by type instead. The rules run on the Server, which must pass, and on <c>Recall.ArchitectureFixtures</c>,
/// which has one violation for each rule and each place that the scan reads, so a broken scan cannot pass with no
/// results.
/// </summary>
public class EntrypointBoundaryTests
{
    /// <summary>
    /// The host wiring of the Server, outside <c>Program</c>. <c>HostExtensions</c> adds OpenTelemetry, Serilog, and
    /// the health checks of the host, and it maps the health endpoints. So it uses <c>HttpContext</c>,
    /// <c>PathString</c>, and the routing types, the same as <c>Program</c>.
    /// </summary>
    private static readonly string[] ServerCompositionRoot =
    [
        "Recall.Server.Resources.Extensions.HostExtensions",
    ];

    /// <summary>
    /// The transport types that the core of the Server can use. <c>ConfigureServices</c> gets a
    /// <c>WebApplicationBuilder</c>, but that type is in <c>Microsoft.AspNetCore.Builder</c>, so it needs no entry.
    /// </summary>
    private static readonly string[] ServerAllowedTransportTypes = [];

    private static readonly Lazy<BoundaryScanner> Server = new(() => new BoundaryScanner(
        typeof(Recall.Server.Resources.PagedList).Assembly,
        "Recall.Server",
        ServerCompositionRoot,
        ServerAllowedTransportTypes));

    private static readonly Lazy<BoundaryScanner> Fixtures = new(() => new BoundaryScanner(
        typeof(FixtureMarker).Assembly,
        FixtureMarker.RootNamespace,
        allowedTransportTypes: [FixtureMarker.AllowedTransportType]));

    [Test]
    public void core_does_not_use_a_transport_library()
    {
        // Act & Assert
        Server.Value.CoreThatUsesATransport().ShouldBeEmpty(
            "Move the transport code to an adapter in the feature folder, or map the transport type to a core type in the adapter.");
    }

    [Test]
    public void core_does_not_use_an_adapter_or_the_shared_plumbing()
    {
        // Act & Assert
        Server.Value.CoreThatUsesAnEntrypoint().ShouldBeEmpty(
            "A feature, a service, or the database must not depend on an entrypoint. Move the shared code to the core.");
    }

    [Test]
    public void adapters_do_not_use_the_database()
    {
        // Act & Assert
        Server.Value.AdaptersThatUseTheDatabase().ShouldBeEmpty(
            "An adapter sends a MediatR request. The handler reads and writes the database.");
    }

    [Test]
    public void only_an_entrypoint_creates_a_command_that_runs_again_when_raced()
    {
        // Act & Assert
        Server.Value.CoreThatCreatesARunAgainRequest().ShouldBeEmpty(
            "A run again discards each tracked change of the scope, also those of the handler that sent the command. " +
            "Send the command from an entrypoint, or call the domain method of the command in the handler.");
    }

    [Test]
    public void each_kind_of_server_type_is_found()
    {
        // Act
        var adapters = Server.Value.AdapterNames();

        // Assert
        adapters.ShouldContain("Recall.Server.Domain.Tenants.Features.AddTenantEndpoint");
        adapters.ShouldContain("Recall.Server.Domain.Tenants.Features.AddTenantTool");

        var core = Server.Value.CoreNames();
        core.ShouldContain("Recall.Server.Databases.AppDbContext");
        core.ShouldContain("Recall.Server.Domain.Tenants.Features.AddTenant");
        core.ShouldContain("Recall.Server.Domain.Users.CurrentUserService");
    }

    [Test]
    public void rule_1_finds_each_kind_of_transport_use()
    {
        // Act & Assert
        Fixtures.Value.CoreThatUsesATransport().ShouldBe(
        [
            "Recall.ArchitectureFixtures.Core.CoreWithAsyncTransportUse",
            "Recall.ArchitectureFixtures.Core.CoreWithGenericConstraint`1",
            "Recall.ArchitectureFixtures.Core.CoreWithHttpResult",
            "Recall.ArchitectureFixtures.Core.CoreWithMethodGenericConstraint",
            "Recall.ArchitectureFixtures.Core.CoreWithNamedAttributeArgument",
            "Recall.ArchitectureFixtures.Core.CoreWithReturnAttribute",
        ]);
    }

    [Test]
    public void rule_2_finds_core_that_uses_an_entrypoint()
    {
        // Act & Assert
        Fixtures.Value.CoreThatUsesAnEntrypoint().ShouldBe(
        [
            "Recall.ArchitectureFixtures.Core.CoreWithAdapter",
            "Recall.ArchitectureFixtures.Core.CoreWithSharedPlumbing",
        ]);
    }

    [Test]
    public void rule_3_finds_adapters_that_use_the_database()
    {
        // Act & Assert
        Fixtures.Value.AdaptersThatUseTheDatabase().ShouldBe(
        [
            "Recall.ArchitectureFixtures.Adapters.EndpointWithDatabase",
            "Recall.ArchitectureFixtures.Adapters.ToolWithDatabase",
            "Recall.ArchitectureFixtures.Resources.Entrypoints.PlumbingWithDatabase",
        ]);
    }

    [Test]
    public void rule_4_finds_core_that_creates_a_command_that_runs_again()
    {
        // Act & Assert
        Fixtures.Value.CoreThatCreatesARunAgainRequest().ShouldBe(
        [
            "Recall.ArchitectureFixtures.Core.CoreWithAsyncRunAgainCommand",
            "Recall.ArchitectureFixtures.Core.CoreWithRunAgainCommand",
            "Recall.ArchitectureFixtures.Core.CoreWithRunAgainStructInPlace",
            "Recall.ArchitectureFixtures.Core.CoreWithRunAgainStructOfDefault",
            "Recall.ArchitectureFixtures.Core.RunAgainCommandWithFactory",
        ]);
    }

    [Test]
    public void the_fixtures_sort_each_kind_of_type()
    {
        // Act
        var adapters = Fixtures.Value.AdapterNames();

        // Assert
        adapters.ShouldContain("Recall.ArchitectureFixtures.Adapters.CleanEndpoint");

        // A clean core type, and a core type that uses only the allowed transport type, are not in rule 1
        var core = Fixtures.Value.CoreNames();
        core.ShouldContain("Recall.ArchitectureFixtures.Core.CleanCore");
        core.ShouldContain("Recall.ArchitectureFixtures.Core.CoreWithAllowedTransportType");
    }
}
