namespace Recall.UnitTests.Auth;

using Recall.Server.Auth;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.Users;
using Shouldly;

public sealed class NewPersonTests
{
    public sealed record Row(string Name, Profile Profile, NewPerson Expected);

    public static IEnumerable<Row> Rows()
    {
        yield return new("all claims", new Profile("Ada", "Lovelace", "Ada King", "ada@example.com", "ada"), new NewPerson("Ada", "Lovelace", "ada@example.com", "ada"));
        yield return new("name only", new Profile(null, null, "Ada King of Lovelace", null, null), new NewPerson("Ada", "King of Lovelace", null, "Ada"));
        yield return new("one word name", new Profile(null, null, "Ada", null, null), new NewPerson("Ada", null, null, "Ada"));
        yield return new("given name does not take the rest of name", new Profile("Ada", null, "Ada King", null, null), new NewPerson("Ada", null, null, "Ada"));
        yield return new("username only", new Profile(null, null, null, null, "ada99"), new NewPerson("ada99", null, null, "ada99"));
        yield return new("email only", new Profile(null, null, null, "ada@example.com", null), new NewPerson("ada", null, "ada@example.com", "ada@example.com"));
        yield return new("nothing", new Profile(null, null, null, null, null), new NewPerson("Recall user", null, null, "Recall user"));
        yield return new("blanks", new Profile("  ", "\t", " ", " ", "\n"), new NewPerson("Recall user", null, null, "Recall user"));
        yield return new("invalid email", new Profile("Ada", null, null, "not an email", null), new NewPerson("Ada", null, null, "Ada"));
        yield return new("trims", new Profile(" Ada ", " Lovelace ", null, " ada@example.com ", " ada "), new NewPerson("Ada", "Lovelace", "ada@example.com", "ada"));
        yield return new("a NUL drops the claim", new Profile("A\0da", "L\0", null, null, "ada"), new NewPerson("ada", null, null, "ada"));
        yield return new("a lone surrogate drops the claim", new Profile("\uD800", null, "Ada", null, null), new NewPerson("Ada", null, null, "Ada"));
    }

    [Test]
    [MethodDataSource(nameof(Rows))]
    public void claims_table(Row row)
    {
        // Act
        var person = NewPerson.FromClaims(row.Profile);

        // Assert
        person.ShouldBe(row.Expected, row.Name);
    }

    [Test]
    public void long_claims_clip_to_the_column_limits_and_pass_the_user_rules()
    {
        // Arrange
        var email = new string('e', 400) + "@example.com";
        var profile = new Profile(new string('g', 500), new string('f', 500), null, email, new string('u', 500));

        // Act
        var person = NewPerson.FromClaims(profile);
        var user = User.CreateFromLogin(person, EntityTenantId(), UserRole.User());

        // Assert
        person.FirstName.Length.ShouldBe(User.FirstNameMaxLength);
        person.LastName!.Length.ShouldBe(User.LastNameMaxLength);
        person.Username.Length.ShouldBe(User.UsernameMaxLength);
        person.Email.ShouldBeNull();
        user.Identifier.ShouldBe(user.Id);
    }

    [Test]
    public void a_clip_does_not_split_a_surrogate_pair()
    {
        // Arrange
        var given = new string('g', User.FirstNameMaxLength - 1) + "\U0001F600";

        // Act
        var person = NewPerson.FromClaims(new Profile(given, null, null, null, null));
        var user = User.CreateFromLogin(person, EntityTenantId(), UserRole.User());

        // Assert
        person.FirstName.Length.ShouldBe(User.FirstNameMaxLength - 1);
        user.FirstName.ShouldBe(person.FirstName);
    }

    [Test]
    public void to_string_of_each_person_type_hides_its_values()
    {
        // Arrange
        var profile = new Profile("Ada", "Lovelace", "Ada King", "ada@example.com", "ada99");
        var identity = new ExternalIdentity("https://issuer.example", "subject-1", profile);
        var person = NewPerson.FromClaims(profile);

        // Act
        var printed = $"{profile} {identity} {person}";

        // Assert
        printed.ShouldNotContain("Ada");
        printed.ShouldNotContain("Lovelace");
        printed.ShouldNotContain("ada@example.com");
        printed.ShouldNotContain("subject-1");
    }

    private static string EntityTenantId() => Recall.Server.Domain.EntityId.New(Tenant.IdPrefix);
}
