namespace Recall.FunctionalTests.FunctionalTests.HealthChecks;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Recall.Server.Resources.Storage;
using Shouldly;
using Sessions;
using TestUtilities;

public class FileStoreStatusTests : TestBase
{
    [Test]
    public async Task a_member_reads_that_the_file_store_takes_writes()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);

        // Act
        var status = await FactoryClient.GetAsync(ApiRoutes.FileStore.Status);

        // Assert
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await status.Content.ReadFromJsonAsync<FileStoreStatusDto>()).ShouldBe(new FileStoreStatusDto(true));
    }

    [Test]
    public async Task a_store_that_refuses_a_write_gives_false_and_no_path_of_the_host()
    {
        // Arrange
        var check = new FileStoreHealthCheck(() => "/srv/recall is full", "/srv/recall", TimeProvider.System);

        // Act
        var reply = await GetFileStoreStatusEndpoint.Handle(check, CancellationToken.None);

        // Assert
        reply.ShouldBeOfType<Ok<FileStoreStatusDto>>().Value.ShouldBe(new FileStoreStatusDto(false));
    }
}
