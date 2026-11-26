namespace Recall.Server.Domain.Users.Dtos;

using EmailAddresses;
using Resources;
using Tenants;

public sealed record InitiateUserAndTenantDto
{
    [RequiredField]
    [SchemaMaxLength(typeof(Tenant), nameof(Tenant.NameMaxLength))]
    public string TenantName { get; init; } = default!;
    [RequiredField]
    [SchemaMaxLength(typeof(User), nameof(User.FirstNameMaxLength))]
    public string FirstName { get; init; } = default!;
    [RequiredField]
    [SchemaMaxLength(typeof(User), nameof(User.LastNameMaxLength))]
    public string LastName { get; init; } = default!;
    [RequiredField]
    [SchemaMaxLength(typeof(EmailAddress), nameof(EmailAddress.MaxLength))]
    public string Email { get; init; } = default!;
    [RequiredField]
    [SchemaMaxLength(typeof(User), nameof(User.UsernameMaxLength))]
    public string Username { get; init; } = default!;
    /// <summary>The roles of the new user. An empty list makes a Member, who has only their own journal.</summary>
    [NamesOf<UserRole>]
    public IReadOnlyList<string> Roles { get; init => field = value ?? []; } = [];
}
