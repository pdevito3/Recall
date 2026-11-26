namespace Recall.Server.Domain.Users;

/// <summary>One role of a user. A user has at most one live row for each role.</summary>
public class UserRoleAssignment : BaseEntity
{
    public const string IdPrefix = "urole";

    [NotSensitive] public string UserId { get; private set; } = default!;
    public User User { get; private set; } = default!;
    [NotSensitive] public UserRole Role { get; private set; } = default!;

    internal static UserRoleAssignment Create(User user, UserRole role)
        => new() { UserId = user.Id, Role = role };

    protected UserRoleAssignment() : base(IdPrefix) { } // EF Core
}
