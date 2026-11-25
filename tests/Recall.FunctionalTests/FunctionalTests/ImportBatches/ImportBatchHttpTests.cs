namespace Recall.FunctionalTests.FunctionalTests.ImportBatches;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Recall.FunctionalTests.Auth;
using Recall.Server.Domain.ImportBatches.Dtos;
using Recall.Server.Domain.ImportBatches.Features;
using Recall.Server.Domain.Users;
using Shouldly;
using TestUtilities;

public class ImportBatchHttpTests : TestBase
{
    private const string Day = "2024-03-01";

    [Test]
    [NotInParallel(nameof(AuthHosts.KeepAlive))]
    [Property("Rule", "IMP-28")]
    public async Task a_put_whose_body_is_more_than_16_mb_gets_413_on_kestrel_and_stores_nothing()
    {
        // Arrange
        var host = AuthHosts.KeepAlive;
        var (_, member) = await PatPeople.AddTenantAsync(host);
        var token = await PatPeople.StartTokenAsync(host, member, new TokenGrant([], JournalAccess.ReadWrite));
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        // Kestrel refuses the body before it reads it, so a client that does not wait for 100 Continue sees a broken pipe
        client.DefaultRequestHeaders.ExpectContinue = true;
        var opened = await client.PostAsJsonAsync(ApiRoutes.ImportBatches.Root, new { name = "Large", timeZone = "UTC" });
        var batch = (await opened.Content.ReadFromJsonAsync<ImportBatchDto>()).ShouldNotBeNull();
        var raw = new string('a', 900_000);
        var entries = Enumerable.Range(0, 19).Select(i => new { clientSessionId = $"large-{i}", date = Day, position = i, raw });
        using var body = new StringContent(JsonSerializer.Serialize(new { entries }), Encoding.UTF8, "application/json");

        // Act
        var response = await client.PutAsync(ApiRoutes.ImportBatches.Entries(batch.Id), body);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>()).ShouldNotBeNull();
        problem.Detail.ShouldBe(PutImportEntriesEndpoint.TooLargeMessage);
        (await client.GetFromJsonAsync<ImportEntryPageDto>(ApiRoutes.ImportBatches.Entries(batch.Id)))!.Entries.ShouldBeEmpty();
    }
}
