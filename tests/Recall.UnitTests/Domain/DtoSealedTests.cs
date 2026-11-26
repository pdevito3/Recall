namespace Recall.UnitTests.Domain;

using Recall.Server.Domain;
using Shouldly;

public class DtoSealedTests
{
    [Test]
    public void every_dto_is_sealed()
    {
        // Arrange
        var dtos = typeof(BaseEntity).Assembly.GetTypes()
            .Where(t => t.Namespace?.EndsWith(".Dtos", StringComparison.Ordinal) == true && t.IsClass && !t.IsNested)
            .Where(t => !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
            .ToList();

        // Act
        var failures = dtos.Where(t => !t.IsSealed && !t.IsAbstract).Select(t => t.FullName).ToList();

        // Assert
        dtos.ShouldNotBeEmpty();
        failures.ShouldBeEmpty("Make each DTO a sealed record, with no base DTO (rule 1 of docs/backend/dtos-and-mappings.md).");
    }
}
