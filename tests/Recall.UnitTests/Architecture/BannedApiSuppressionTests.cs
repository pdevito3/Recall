namespace Recall.UnitTests.Architecture;

using Shouldly;

public class BannedApiSuppressionTests
{
    private static readonly string[] FilesThatSuppressTheBan =
    [
        "Recall.Server/Auth/SessionStore.cs",
        "Recall.Server/Databases/Sqlite/SqliteFile.cs",
        "Recall.Server/Domain/DPoPProofs/Features/DeleteExpiredDPoPProofs/DeleteExpiredDPoPProofs.cs",
        "Recall.Server/Domain/DPoPProofs/Features/RegisterDPoPProof/RegisterDPoPProof.cs",
        "Recall.Server/Databases/Auditing/AuditVocabulary.cs",
        "Recall.Server/Resources/Entrypoints/Http/EndpointDiscovery.cs",
        "Recall.Server/Resources/RequestValidationBehavior.cs",
    ];

    private static readonly string[] SettingsExtensions = [".cs", ".csproj", ".props", ".editorconfig", ".globalconfig"];

    [Test]
    public void only_the_reviewed_files_suppress_a_banned_api()
    {
        // Arrange
        var root = FindRepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "Recall.Server"), "*", SearchOption.AllDirectories)
            .Concat([Path.Combine(root, ".editorconfig"), Path.Combine(root, "Directory.Build.props")])
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Where(file => SettingsExtensions.Contains(Path.GetExtension(file)))
            .Where(file => !file.Split('/').Intersect(["bin", "obj"]).Any());

        // Act
        var suppressing = files
            .Where(file => File.ReadAllText(Path.Combine(root, file)).Contains("RS0030", StringComparison.Ordinal))
            .ToList();

        // Assert
        suppressing.ShouldBe(FilesThatSuppressTheBan, ignoreOrder: true);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Recall.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Could not find Recall.slnx above the test output directory.");
    }
}
