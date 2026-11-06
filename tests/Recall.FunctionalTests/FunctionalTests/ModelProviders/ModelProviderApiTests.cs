namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Recall.FakeModelProvider;
using Recall.FunctionalTests.FunctionalTests.Sessions;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.ModelProviders.Dtos;
using Shouldly;
using TestUtilities;

public class ModelProviderApiTests : TestBase
{
    private async Task<(FakeModelProvider Fake, string Key)> StartAdminAsync()
    {
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var key = CleanupScenario.NewKey();
        return (await FakeModelProvider.StartAsync(key), key);
    }

    private async Task<ModelProviderListDto> ListAsync()
        => (await FactoryClient.GetFromJsonAsync<ModelProviderListDto>(ApiRoutes.ModelProviders.Root)).ShouldNotBeNull();

    [Test]
    public async Task the_database_and_each_answer_hold_no_plain_key()
    {
        // Arrange
        var (fake, key) = await StartAdminAsync();
        await using var _ = fake;

        // Act
        var added = await FactoryClient.PostAsJsonAsync(ApiRoutes.ModelProviders.Root, new { kind = nameof(ModelProviderKind.Anthropic), name = "Mine", baseUrl = fake.AnthropicBaseUrl, apiKey = key });
        var addedBody = await added.Content.ReadAsStringAsync();
        var listBody = await FactoryClient.GetStringAsync(ApiRoutes.ModelProviders.Root);
        var table = await CleanupScenario.DumpOfTableAsync("model_providers");

        // Assert
        added.StatusCode.ShouldBe(HttpStatusCode.Created, addedBody);
        addedBody.ShouldNotContain(key);
        listBody.ShouldNotContain(key);
        table.ShouldNotContain(key);
        table.ShouldContain("CfDJ8");
    }

    [Test]
    public async Task the_test_button_gives_the_models_and_a_wrong_key_gives_the_words_of_the_provider()
    {
        // Arrange
        var (fake, key) = await StartAdminAsync();
        await using var _ = fake;
        var good = await CleanupScenario.ConnectAsync(FactoryClient, ModelProviderKind.Anthropic, fake.AnthropicBaseUrl, key);
        var bad = await CleanupScenario.ConnectAsync(FactoryClient, ModelProviderKind.OpenAiCompatible, fake.CompatibleBaseUrl, CleanupScenario.NewKey());

        // Act
        var worked = await FactoryClient.PostAsync(ApiRoutes.ModelProviders.Test(good.Id), null);
        var refused = await FactoryClient.PostAsync(ApiRoutes.ModelProviders.Test(bad.Id), null);

        // Assert
        var models = (await worked.Content.ReadFromJsonAsync<ModelProviderTestResultDto>()).ShouldNotBeNull();
        models.Ok.ShouldBeTrue();
        models.Models.Select(model => model.Id).ShouldBe([FakeModelProvider.ModelId, FakeModelProvider.SecondModelId]);
        refused.StatusCode.ShouldBe(HttpStatusCode.OK);
        var failure = (await refused.Content.ReadFromJsonAsync<ModelProviderTestResultDto>()).ShouldNotBeNull();
        failure.Ok.ShouldBeFalse();
        failure.Failure.ShouldNotBeNull().Message.ShouldContain("Incorrect API key");
    }

    [Test]
    public async Task the_newest_activation_is_the_active_provider_and_a_delete_falls_back()
    {
        // Arrange
        var (fake, key) = await StartAdminAsync();
        await using var _ = fake;
        var first = await CleanupScenario.ConnectAsync(FactoryClient, ModelProviderKind.Anthropic, fake.AnthropicBaseUrl, key, "first");
        var second = await CleanupScenario.ConnectAsync(FactoryClient, ModelProviderKind.OpenAiCompatible, fake.CompatibleBaseUrl, key, "second");
        var firstWithModel = await CleanupScenario.PickModelAsync(FactoryClient, first, FakeModelProvider.ModelId);

        // Act
        var whileFirst = await ListAsync();
        await CleanupScenario.PickModelAsync(FactoryClient, second, FakeModelProvider.ModelId);
        var whileSecond = await ListAsync();
        var deleted = await FactoryClient.DeleteAsync(ApiRoutes.ModelProviders.GetRecord(second.Id));
        var afterDelete = await ListAsync();

        // Assert
        whileFirst.Items.Single(p => p.IsActive).Id.ShouldBe(firstWithModel.Id);
        whileSecond.Items.Single(p => p.IsActive).Id.ShouldBe(second.Id);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        afterDelete.Items.Single(p => p.IsActive).Id.ShouldBe(first.Id);
    }

    [Test]
    public async Task an_update_with_no_key_keeps_the_key_and_a_delete_forgets_it()
    {
        // Arrange
        var (fake, key) = await StartAdminAsync();
        await using var _ = fake;
        var provider = await CleanupScenario.ConnectAsync(FactoryClient, ModelProviderKind.Anthropic, fake.AnthropicBaseUrl, key);

        // Act
        var updated = await CleanupScenario.PickModelAsync(FactoryClient, provider, FakeModelProvider.ModelId);
        var stillWorks = await FactoryClient.PostAsync(ApiRoutes.ModelProviders.Test(provider.Id), null);
        var protectedBefore = await JournalKeyOfAsync(provider.Id);
        await FactoryClient.DeleteAsync(ApiRoutes.ModelProviders.GetRecord(updated.Id));
        var protectedAfter = await JournalKeyOfAsync(provider.Id);

        // Assert
        (await stillWorks.Content.ReadFromJsonAsync<ModelProviderTestResultDto>()).ShouldNotBeNull().Ok.ShouldBeTrue();
        protectedBefore.ShouldNotBeNull();
        protectedAfter.ShouldBeNull();
    }

    private static Task<string?> JournalKeyOfAsync(string id) => ExecuteDbContextAsync(db => db.ModelProviders
        .IgnoreQueryFilters()
        .Where(p => p.Id == id)
        .Select(p => p.ProtectedApiKey)
        .FirstOrDefaultAsync());

    [Test]
    public async Task a_provider_of_an_unknown_kind_or_with_a_bad_address_gets_422()
    {
        // Arrange
        var (fake, key) = await StartAdminAsync();
        await using var _ = fake;

        // Act
        var kind = await FactoryClient.PostAsJsonAsync(ApiRoutes.ModelProviders.Root, new { kind = "Other", name = "Mine", apiKey = key });
        var address = await FactoryClient.PostAsJsonAsync(ApiRoutes.ModelProviders.Root, new { kind = nameof(ModelProviderKind.OpenAiCompatible), name = "Mine", baseUrl = "not an address" });

        // Assert
        kind.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        address.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    public async Task a_key_under_another_ring_shows_as_unreadable_and_the_list_says_that_the_ring_changed()
    {
        // Arrange
        var (fake, key) = await StartAdminAsync();
        await using var _ = fake;
        var provider = await CleanupScenario.ConnectAsync(FactoryClient, ModelProviderKind.Anthropic, fake.AnthropicBaseUrl, key);

        // Act
        await CleanupScenario.SetProtectedKeyAsync(provider.Id, CleanupScenario.ProtectedKeyUnderAnotherRing());
        var list = await ListAsync();
        var test = await FactoryClient.PostAsync(ApiRoutes.ModelProviders.Test(provider.Id), null);

        // Assert
        list.KeyRing.ShouldBe(nameof(KeyRingState.Changed));
        list.Items.Single().KeyState.ShouldBe(nameof(ModelProviderKeyState.Unreadable));
        (await test.Content.ReadFromJsonAsync<ModelProviderTestResultDto>()).ShouldNotBeNull().Failure.ShouldNotBeNull().Message.ShouldContain("Enter the API key again");
    }
}
