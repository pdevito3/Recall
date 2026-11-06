namespace Recall.UnitTests.Auth;

using System.Runtime.Versioning;
using Recall.Server;
using Shouldly;

[UnsupportedOSPlatform("windows")]
public class OwnerOnlyFilesTests
{
    private const UnixFileMode Wide = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("recall_modes_").FullName;

        public string NewFile(string name)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, "x");
            File.SetUnixFileMode(path, Wide);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    [Test]
    public void tighten_sets_the_modes_and_refuses_nothing()
    {
        // Arrange
        using var tree = new TempTree();
        var file = tree.NewFile("recall.db");

        // Act
        var refused = OwnerOnlyFiles.Tighten([tree.Root], [file, $"{file}-absent"], OwnerOnlyFiles.ModeAccess.Real);

        // Assert
        refused.ShouldBeEmpty();
        File.GetUnixFileMode(tree.Root).ShouldBe(OwnerOnlyFiles.DirectoryMode);
        File.GetUnixFileMode(file).ShouldBe(OwnerOnlyFiles.FileMode);
    }

    [Test]
    public void a_volume_that_ignores_modes_is_reported_and_does_not_throw()
    {
        // Arrange
        using var tree = new TempTree();
        var file = tree.NewFile("recall.db");
        var ignores = new OwnerOnlyFiles.ModeAccess(File.GetUnixFileMode, (_, _) => { });

        // Act
        var refused = OwnerOnlyFiles.Tighten([], [file], ignores);

        // Assert
        refused.ShouldBe([file]);
        File.GetUnixFileMode(file).ShouldBe(Wide);
    }

    [Test]
    public void a_volume_that_throws_is_reported_and_does_not_throw()
    {
        // Arrange
        using var tree = new TempTree();
        var file = tree.NewFile("recall.db");
        var throws = new OwnerOnlyFiles.ModeAccess(File.GetUnixFileMode, (_, _) => throw new UnauthorizedAccessException());

        // Act
        var refused = OwnerOnlyFiles.Tighten([], [file], throws);

        // Assert
        refused.ShouldBe([file]);
    }

    [Test]
    public void a_link_is_not_followed()
    {
        // Arrange
        using var tree = new TempTree();
        var target = tree.NewFile("target");
        var link = Path.Combine(tree.Root, "link");
        File.CreateSymbolicLink(link, target);

        // Act
        var refused = OwnerOnlyFiles.Tighten([], [link], OwnerOnlyFiles.ModeAccess.Real);

        // Assert
        refused.ShouldBeEmpty();
        File.GetUnixFileMode(target).ShouldBe(Wide);
    }
}
