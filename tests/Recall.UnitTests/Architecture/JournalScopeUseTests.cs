namespace Recall.UnitTests.Architecture;

using Shouldly;

/// <summary>
/// A journal scope reads the rows of a User with nobody signed in. Only a job that the owner started may open one, so no
/// other code can read a journal by passing an id.
/// </summary>
public class JournalScopeUseTests
{
    private const string JobsFolder = "Domain/Sessions/Jobs/";
    private const string DefinitionFile = "Domain/Users/JournalWorkAccess.cs";

    [Test]
    public void only_a_job_of_a_session_opens_a_journal_scope()
    {
        // Arrange
        var server = Path.Combine(FindRepositoryRoot(), "Recall.Server");

        // Act
        var users = Directory.EnumerateFiles(server, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(server, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(file => File.ReadAllText(Path.Combine(server, file)).Contains("CreateJournalScope(", StringComparison.Ordinal))
            .Where(file => file != DefinitionFile && !file.StartsWith(JobsFolder, StringComparison.Ordinal))
            .ToList();

        // Assert
        users.ShouldBeEmpty("See docs/backend/background-jobs.md.");
    }

    [Test]
    public void cleanup_code_uses_no_query_filter_bypass()
    {
        // Arrange
        var server = Path.Combine(FindRepositoryRoot(), "Recall.Server");
        string[] folders = ["Domain/Sessions/Jobs", "Domain/Sessions/Cleanup"];

        // Act
        var users = folders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(server, folder), "*.cs"))
            .Where(file => File.ReadAllText(file).Contains("IgnoreQueryFilters", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        // Assert
        users.ShouldBeEmpty();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Recall.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Recall.slnx");
    }
}
