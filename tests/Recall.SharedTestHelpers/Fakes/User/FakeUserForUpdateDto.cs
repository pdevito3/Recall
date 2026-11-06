namespace Recall.SharedTestHelpers.Fakes.User;

using AutoBogus;
using Recall.Server.Domain.Users.Dtos;

public sealed class FakeUserForUpdateDto : AutoFaker<UserForUpdateDto>
{
    public FakeUserForUpdateDto()
    {
        RuleFor(x => x.FirstName, UserRules.FirstName);
        RuleFor(x => x.LastName, UserRules.LastName);
        RuleFor(x => x.Email, UserRules.Email);
        RuleFor(x => x.Username, UserRules.Username);
        RuleFor(x => x.Version, _ => 0u);
    }
}
