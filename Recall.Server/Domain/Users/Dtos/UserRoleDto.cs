namespace Recall.Server.Domain.Users.Dtos;

using Resources;

public sealed record UserRoleDto
{
    [RequiredField]
    [NamesOf<UserRole>]
    public string Role { get; init; } = default!;
}
