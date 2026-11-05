namespace Recall.UnitTests.Architecture;

using System.Reflection;
using Grpc.Core;
using ModelContextProtocol.Server;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Recall.ArchitectureFixtures;
using Recall.Server.Databases;
using Recall.Server.Databases.Auditing;
using Recall.Server.Domain;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sync;
using Recall.Server.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

public class JournalRulesTests
{
    private static readonly string[] JournalNamespaces =
    [
        "Recall.Server.Domain.Sessions",
        "Recall.Server.Domain.People",
        "Recall.Server.Domain.Corrections",
        "Recall.Server.Domain.UserSettings",
        "Recall.Server.Domain.Sync",
    ];

    [Test]
    [Property("Rule", "SES-14")]
    public void each_table_of_a_journal_domain_has_the_owner_filter()
    {
        // Arrange
        using var context = new AppDbContextFactory().CreateDbContext([]);

        // Act
        var missing = context.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.ClrType.Namespace is { } name && JournalNamespaces.Any(root => name.StartsWith(root, StringComparison.Ordinal)))
            .Where(e => e.FindDeclaredQueryFilter(QueryFilterNames.JournalOwner) is null)
            .Select(e => e.ClrType.Name);

        // Assert
        missing.ShouldBeEmpty("A journal table needs IJournalOwned, or ApplyJournalOwner when it has no owner column.");
    }

    [Test]
    [Property("Rule", "SES-14")]
    public void no_code_of_the_server_turns_off_the_owner_filter()
    {
        // Arrange
        using var context = new AppDbContextFactory().CreateDbContext([]);

        // Act
        var bypasses = FilterBypasses(typeof(AppDbContext).Assembly, JournalTypes(context.Model));

        // Assert
        bypasses.ShouldBeEmpty("Only the owner reads a journal. Read through the filter, and do not call IgnoreQueryFilters on a journal type.");
    }

    [Test]
    public void rule_finds_a_call_that_turns_off_the_filters_on_a_journal_type()
    {
        // Arrange
        using var context = new AppDbContextFactory().CreateDbContext([]);

        // Act
        var bypasses = FilterBypasses(typeof(FixtureMarker).Assembly, JournalTypes(context.Model));

        // Assert
        bypasses.ShouldBe(["Recall.ArchitectureFixtures.Core.JournalFilter.ReadsEachJournal reads Session"]);
    }

    [Test]
    [Property("Rule", "SES-19")]
    public void the_audit_log_records_only_the_tenant_and_the_owner_of_a_journal_row()
    {
        // Arrange
        using var context = new AppDbContextFactory().CreateDbContext([]);
        var audit = AuditModel.Of(context.Model);

        // Act
        var recorded = context.Model.GetEntityTypes()
            .Where(e => e.ClrType.IsAssignableTo(typeof(IJournalOwned)) && e.ClrType != typeof(SyncChange))
            .SelectMany(e => audit.ValuesOf(e).Where(v => v.IsRecorded).Select(v => v.Path))
            .Distinct()
            .Order(StringComparer.Ordinal);

        // Assert
        recorded.ShouldBe(["TenantId", "UserId"]);
    }

    [Test]
    [Property("Rule", "SES-15")]
    public void no_mcp_tool_or_grpc_service_is_in_a_journal_domain()
    {
        // Arrange
        var types = typeof(Session).Assembly.GetTypes()
            .Where(t => t.Namespace is { } name && JournalNamespaces.Any(root => name.StartsWith(root, StringComparison.Ordinal)));

        // Act
        var adapters = types
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null
                || t.BaseType?.GetCustomAttribute<BindServiceMethodAttribute>() is not null)
            .Select(t => t.FullName);

        // Assert
        adapters.ShouldBeEmpty("A machine has no journal, so a journal route has only the HTTP adapter of the person who owns it.");
    }

    private static HashSet<string> JournalTypes(IModel model)
        => model.GetEntityTypes()
            .Where(e => e.FindDeclaredQueryFilter(QueryFilterNames.JournalOwner) is not null)
            .Select(e => e.ClrType.FullName!)
            .ToHashSet(StringComparer.Ordinal);

    private static string[] FilterBypasses(Assembly assembly, HashSet<string> journalTypes)
    {
        using var module = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(assembly.Location)));
        return module.GetTypes()
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions
                .Select(IgnoredEntityOf)
                .OfType<string>()
                .Where(journalTypes.Contains)
                .Select(entity => $"{Outermost(method.DeclaringType).FullName}.{method.Name} reads {entity[(entity.LastIndexOf('.') + 1)..]}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string? IgnoredEntityOf(Instruction instruction)
        => instruction is { OpCode.Code: Code.Call or Code.Callvirt, Operand: GenericInstanceMethod { Name: nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters), GenericArguments: [var entity] } }
            ? entity.FullName
            : null;

    private static TypeDefinition Outermost(TypeDefinition type)
    {
        while (type.DeclaringType is not null)
            type = type.DeclaringType;
        return type;
    }
}
