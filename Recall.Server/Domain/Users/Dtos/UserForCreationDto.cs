namespace Recall.Server.Domain.Users.Dtos;

using EmailAddresses;
using Resources;

public sealed record UserForCreationDto
{
    /// <summary>
    /// Only for a caller that sees all tenants, such as an allow-listed machine client. That caller must name the tenant.
    /// A user always adds to their own tenant. If a user sends a tenant, it must be their own tenant.
    /// </summary>
    public string? TenantId { get; init; }
    [RequiredField]
    [SchemaMaxLength(typeof(User), nameof(User.FirstNameMaxLength))]
    public string FirstName { get; init; } = default!;
    [SchemaMaxLength(typeof(User), nameof(User.LastNameMaxLength))]
    public string? LastName { get; init; }
    [SchemaMaxLength(typeof(EmailAddress), nameof(EmailAddress.MaxLength))]
    public string? Email { get; init; }
    [RequiredField]
    [SchemaMaxLength(typeof(User), nameof(User.UsernameMaxLength))]
    public string Username { get; init; } = default!;
    /// <summary>The roles of the new user. An empty list makes a Member, who has only their own journal.</summary>
    [NamesOf<UserRole>]
    public IReadOnlyList<string> Roles { get; init => field = value ?? []; } = [];
}
