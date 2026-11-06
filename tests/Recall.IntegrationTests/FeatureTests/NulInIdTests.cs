namespace Recall.IntegrationTests.FeatureTests;

using Recall.Server.Domain.Agents.Features;
using Recall.Server.Domain.Tenants.Dtos;
using Recall.Server.Domain.Tenants.Features;
using Recall.Server.Domain.Users.Dtos;
using Recall.Server.Domain.Users.Features;
using Recall.Server.Exceptions;
using MediatR;
using Shouldly;

public class NulInIdTests : TestBase
{
    private const string NulUserId = "usr_a\0b";
    private const string NulTenantId = "ten_a\0b";
    private const string NulAgentId = "agt_a\0b";

    public static IEnumerable<Func<(string Feature, object Request)>> RequestsThatLoadById()
    {
        yield return () => (nameof(GetUser), new GetUser.Query(NulUserId));
        yield return () => (nameof(UpdateUser), new UpdateUser.Command(NulUserId, new UserForUpdateDto
        {
            FirstName = "Ada", LastName = "Lovelace", Email = "ada@example.com", Username = "ada", Version = 1,
        }));
        yield return () => (nameof(DeleteUser), new DeleteUser.Command(NulUserId));
        yield return () => (nameof(UpdateUserRole), new UpdateUserRole.Command(NulUserId, "User"));
        yield return () => (nameof(AddUserPermission), new AddUserPermission.Command(NulUserId, "read_users"));
        yield return () => (nameof(RemoveUserPermission), new RemoveUserPermission.Command(NulUserId, "read_users"));
        yield return () => (nameof(GetTenant), new GetTenant.Query(NulTenantId));
        yield return () => (nameof(UpdateTenant), new UpdateTenant.Command(NulTenantId, new TenantForUpdateDto { Name = "Renamed", Version = 1 }));
        yield return () => (nameof(DeleteTenant), new DeleteTenant.Command(NulTenantId));
        yield return () => (nameof(GetAgent), new GetAgent.Query(NulAgentId));
        yield return () => (nameof(DeleteAgent), new DeleteAgent.Command(NulAgentId));
        yield return () => (nameof(AddAgentPermission), new AddAgentPermission.Command(NulAgentId, "read_users"));
        yield return () => (nameof(RemoveAgentPermission), new RemoveAgentPermission.Command(NulAgentId, "read_users"));
    }

    [Test]
    [MethodDataSource(nameof(RequestsThatLoadById))]
    public async Task a_nul_in_the_id_is_not_found(string feature, object request)
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();

        // Act
        var act = () => testingServiceScope.GetService<ISender>().Send(request);

        // Assert
        await act.ShouldThrowAsync<NotFoundException>();
    }
}
