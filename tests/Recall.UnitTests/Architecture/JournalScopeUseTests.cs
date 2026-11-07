namespace Recall.UnitTests.Architecture;

using Shouldly;

/// <summary>
/// A journal scope reads the rows of a User with nobody signed in. Only the background work of a Session or a Recording
/// may open one, so no other code can read a journal by passing an id.
/// </summary>
public class JournalScopeUseTests
{
    private const string DefinitionFile = "Domain/Tenants/SystemTenantAccess.cs";

    private static readonly string[] JobFolders = ["Domain/Sessions/Jobs/", "Domain/Recordings/Jobs/"];

    [Test]
    public void only_a_job_folder_opens_a_journal_scope()
    {
        // Arrange
        var server = Path.Combine(FindRepositoryRoot(), "Recall.Server");

        // Act
        var users = Directory.EnumerateFiles(server, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(server, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(file => File.ReadAllText(Path.Combine(server, file)).Contains("CreateJournalScope(", StringComparison.Ordinal))
            .Where(file => file != DefinitionFile && !JobFolders.Any(folder => file.StartsWith(folder, StringComparison.Ordinal)))
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
