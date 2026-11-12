namespace Recall.FunctionalTests.FunctionalTests.Corrections;

using System.Net;
using System.Net.Http.Json;
using Recall.Server.Databases;
using Recall.Server.Domain.Corrections.Dtos;
using Sessions;
using Shouldly;
using TestUtilities;

public class CorrectionCreateReplayTests : TestBase
{
    private readonly string DeviceId = $"cor_{Guid.CreateVersion7():N}";

    [Test]
    public async Task a_create_keeps_the_id_of_the_device_and_a_replay_gives_200_with_the_same_correction()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var body = new { id = DeviceId, canonicalTerm = "Kubernetes", mishearings = new[] { "cube net ease" }, hardReplace = true };
        var first = await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, body);

        // Act
        var replay = await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, body with { canonicalTerm = "Changed" });
        var replayed = await replay.Content.ReadFromJsonAsync<CorrectionDto>();
        var list = await FactoryClient.GetFromJsonAsync<CorrectionDto[]>(ApiRoutes.Corrections.Root);

        // Assert
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.StatusCode.ShouldBe(HttpStatusCode.OK);
        replayed!.Id.ShouldBe(DeviceId);
        replayed.CanonicalTerm.ShouldBe("Kubernetes");
        replayed.Mishearings.ShouldBe(["cube net ease"]);
        replayed.HardReplace.ShouldBeTrue();
        list.ShouldHaveSingleItem().Id.ShouldBe(DeviceId);
    }

    [Test]
    public async Task a_replay_after_a_delete_gives_409_and_does_not_bring_the_correction_back()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var body = new { id = DeviceId, canonicalTerm = "Kubernetes" };
        await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, body);
        await FactoryClient.DeleteAsync(ApiRoutes.Corrections.GetRecord(DeviceId));

        // Act
        var replay = await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, body);
        var list = await FactoryClient.GetFromJsonAsync<CorrectionDto[]>(ApiRoutes.Corrections.Root);

        // Assert
        await ShouldRefuseTheId(replay);
        list.ShouldBeEmpty();
    }

    [Test]
    public async Task the_id_of_a_correction_of_another_user_gives_409_and_does_not_change_it()
    {
        // Arrange
        var (admin, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, new { id = DeviceId, canonicalTerm = "Kubernetes" });
        FactoryClient.SignInAsAppUser(admin.Identifier);

        // Act
        var taken = await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, new { id = DeviceId, canonicalTerm = "Other" });
        var adminList = await FactoryClient.GetFromJsonAsync<CorrectionDto[]>(ApiRoutes.Corrections.Root);
        FactoryClient.SignInAsAppUser(member.Identifier);
        var memberCorrection = await FactoryClient.GetFromJsonAsync<CorrectionDto>(ApiRoutes.Corrections.GetRecord(DeviceId));

        // Assert
        await ShouldRefuseTheId(taken);
        adminList.ShouldBeEmpty();
        memberCorrection!.CanonicalTerm.ShouldBe("Kubernetes");
    }

    [Test]
    public async Task the_deleted_id_of_another_user_gives_409_and_their_feed_keeps_the_delete()
    {
        // Arrange
        var (admin, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, new { id = DeviceId, canonicalTerm = "Kubernetes" });
        await FactoryClient.DeleteAsync(ApiRoutes.Corrections.GetRecord(DeviceId));
        FactoryClient.SignInAsAppUser(admin.Identifier);

        // Act
        var taken = await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, new { id = DeviceId, canonicalTerm = "Other" });
        var adminList = await FactoryClient.GetFromJsonAsync<CorrectionDto[]>(ApiRoutes.Corrections.Root);
        FactoryClient.SignInAsAppUser(member.Identifier);
        var memberFeed = await FactoryClient.PullAsync();

        // Assert
        await ShouldRefuseTheId(taken);
        adminList.ShouldBeEmpty();
        memberFeed.Tombstones.ShouldContain(t => t.EntityType == "Correction" && t.Id == DeviceId);
    }

    [Test]
    public async Task replays_sent_at_the_same_time_make_one_correction_and_each_gets_it()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var ids = Enumerable.Range(0, 10).Select(_ => $"cor_{Guid.CreateVersion7():N}").ToList();

        // Act
        var answers = new List<HttpResponseMessage>();
        foreach (var id in ids)
        {
            var body = new { id, canonicalTerm = "Kubernetes" };
            var sends = Enumerable.Range(0, 4).Select(_ => FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, body));
            answers.AddRange(await Task.WhenAll(sends));
        }
        var list = await FactoryClient.GetFromJsonAsync<CorrectionDto[]>(ApiRoutes.Corrections.Root);

        // Assert
        var refused = answers.Where(response => response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK));
        (await Task.WhenAll(refused.Select(async response => $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}")))
            .ShouldBeEmpty();
        answers.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(ids.Count);
        list!.Select(c => c.Id).ShouldBe(ids, ignoreOrder: true);
    }

    [Test]
    public async Task an_id_of_another_kind_gives_422()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);

        // Act
        var response = await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root, new { id = "ses_0199f2a1b2c34d5e8f90a1b2c3d4e5f6", canonicalTerm = "Kubernetes" });
        var list = await FactoryClient.GetFromJsonAsync<CorrectionDto[]>(ApiRoutes.Corrections.Root);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        list.ShouldBeEmpty();
    }

    private static async Task ShouldRefuseTheId(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain(JournalTriggers.IdTakenMessage);
    }
}
