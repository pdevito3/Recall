namespace Recall.Bff.Tests.Unit;

using System.ComponentModel.DataAnnotations;
using Recall.Bff.Authentication;
using Shouldly;

public class RefreshOptionsTests
{
    [Test]
    public void the_defaults_are_valid()
    {
        // Act & Assert
        Errors(new RefreshOptions()).ShouldBeEmpty();
    }

    // The same check as ValidateDataAnnotations
    private static List<string?> Errors(RefreshOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results.Select(r => r.ErrorMessage).ToList();
    }
}
