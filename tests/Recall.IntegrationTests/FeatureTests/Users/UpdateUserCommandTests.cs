namespace Recall.IntegrationTests.FeatureTests.Users;

using Recall.Server.Domain.Users.Features;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Fakes.User;
using Microsoft.EntityFrameworkCore;
using Shouldly;

public class UpdateUserCommandTests : TestBase
{
    [Test]
    public async Task can_update_existing_user()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var fakeUser)
            .BuildAsync();

        var updateDto = new FakeUserForUpdateDto()
            .RuleFor(x => x.FirstName, _ => "UpdatedFirst")
            .RuleFor(x => x.LastName, _ => "UpdatedLast")
            .Generate() with { Version = fakeUser.Version };

        // Act
        var command = new UpdateUser.Command(fakeUser.Id, updateDto);
        var userReturned = await testingServiceScope.SendAsync(command);

        // Assert
        var userInDb = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.Users.FirstOrDefaultAsync(u => u.Id == fakeUser.Id));

        userInDb.ShouldNotBeNull();
        userInDb.FirstName.ShouldBe("UpdatedFirst");
        userInDb.LastName.ShouldBe("UpdatedLast");
        userReturned.FirstName.ShouldBe("UpdatedFirst");
        userReturned.LastName.ShouldBe("UpdatedLast");
    }

    [Test]
    public async Task can_clear_email_and_last_name_and_read_the_user_back()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var fakeUser)
            .BuildAsync();

        var updateDto = new FakeUserForUpdateDto().Generate() with
        {
            Version = fakeUser.Version,
            Email = null,
            LastName = null,
        };

        // Act
        await testingServiceScope.SendAsync(new UpdateUser.Command(fakeUser.Id, updateDto));
        var userRead = await testingServiceScope.SendAsync(new GetUser.Query(fakeUser.Id));
        var usersListed = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.Users.AsNoTracking().Where(u => u.Id == fakeUser.Id).ToListAsync());

        // Assert
        userRead.Email.ShouldBeNull();
        userRead.LastName.ShouldBeNull();
        userRead.FullName.ShouldBe(updateDto.FirstName);
        usersListed.ShouldHaveSingleItem().Email.ShouldBeNull();
        usersListed[0].LastName.ShouldBeNull();
    }

    [Test]
    public async Task can_update_user_email()
    {
        // Arrange
        var testingServiceScope = new TestingServiceScope();
        await testingServiceScope.StartGraph(out _)
            .AddUser(out var fakeUser)
            .BuildAsync();

        var updateDto = new FakeUserForUpdateDto()
            .RuleFor(x => x.Email, _ => "newemail@example.com")
            .Generate() with { Version = fakeUser.Version };

        // Act
        var command = new UpdateUser.Command(fakeUser.Id, updateDto);
        var userReturned = await testingServiceScope.SendAsync(command);

        // Assert
        var userInDb = await testingServiceScope.ExecuteDbContextAsync(db =>
            db.Users.FirstOrDefaultAsync(u => u.Id == fakeUser.Id));

        userInDb.ShouldNotBeNull();
        userInDb.Email!.Value.ShouldBe("newemail@example.com");
    }
}
