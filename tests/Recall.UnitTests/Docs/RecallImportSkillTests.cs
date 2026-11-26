namespace Recall.UnitTests.Docs;

using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Recall.Server.Domain.ImportBatches;
using Recall.Server.Domain.Sessions.Features;
using Recall.Server.Domain.Sessions.Metadata;
using Shouldly;

/// <summary>
/// The SPA ships <c>frontend/src/domain/imports/recall-import.md</c>, and a person pastes it into an agent. The agent
/// knows the tools and the Moods of Recall only from that text, so these tests compare it with the Server.
/// </summary>
public partial class RecallImportSkillTests
{
    [Test]
    [Property("Rule", "IMP-30")]
    public void the_recall_import_skill_names_exactly_the_mcp_tools_of_the_import()
    {
        // Arrange
        var importTools = ImportToolNames();
        var skill = SkillText();

        // Act
        var named = ToolLikeName().Matches(skill).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

        // Assert
        importTools.ShouldNotBeEmpty();
        importTools.Except(named).Order(StringComparer.Ordinal)
            .ShouldBeEmpty("Each MCP tool of the ImportBatches domain, and list_journal_days, is a tool of the import. Name it in recall-import.md, so that an agent with the skill knows it.");
        named.Except(importTools).Order(StringComparer.Ordinal)
            .ShouldBeEmpty("recall-import.md names a tool that is not an MCP tool of the import. Use the name of the tool as the Server registers it, and write any other word without an underscore.");
    }

    [Test]
    [Property("Rule", "IMP-30")]
    public void the_recall_import_skill_lists_exactly_the_moods_that_recall_knows()
    {
        // Arrange
        var skill = SkillText();

        // Act
        var line = MoodLine().Match(skill);

        // Assert
        line.Success.ShouldBeTrue("recall-import.md has the line 'Use only these Moods: A, B, C.'");
        line.Groups["moods"].Value.Split(", ").Order(StringComparer.Ordinal)
            .ShouldBe(Mood.Names.Order(StringComparer.Ordinal), "The skill lists each Mood of Mood.Names, and no other.");
    }

    // The tools of the import are the MCP tools of the ImportBatches domain, and the journal read that finds an overlap with
    // the journal. Another journal tool, such as a later list of Sessions, is not for the import, so the skill need not name it.
    private static HashSet<string> ImportToolNames()
    {
        var domain = typeof(ImportBatch).Namespace!;
        return typeof(ImportBatch).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .Where(t => t == typeof(GetJournalDaysTool) || t.Namespace == domain || t.Namespace!.StartsWith(domain + ".", StringComparison.Ordinal))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string SkillText()
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), "frontend", "src", "domain", "imports", "recall-import.md"));

    // A snake_case word, such as put_import_entries, is the shape of each MCP tool name of the Server.
    [GeneratedRegex(@"\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b")]
    private static partial Regex ToolLikeName();

    [GeneratedRegex(@"Use only these Moods: (?<moods>[^.\n]+)\.")]
    private static partial Regex MoodLine();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Recall.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not find Recall.slnx above the test output directory.");
    }
}
