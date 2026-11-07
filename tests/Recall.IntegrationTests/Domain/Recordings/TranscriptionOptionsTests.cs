namespace Recall.IntegrationTests.Domain.Recordings;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Recall.Server.Domain.Recordings.Transcription;
using Shouldly;

public class TranscriptionOptionsTests
{
    private static WebApplication BuildWith(string minutes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Transcription:ModelRetryMinutes"] = minutes });
        builder.AddTranscription();
        return builder.Build();
    }

    [Test]
    [Arguments("0")]
    [Arguments("-1")]
    public async Task a_start_with_a_model_retry_below_one_minute_fails_and_names_the_setting(string minutes)
    {
        // Arrange
        await using var app = BuildWith(minutes);

        // Act
        var failure = Should.Throw<OptionsValidationException>(() => app.Services.GetRequiredService<IStartupValidator>().Validate());

        // Assert
        failure.Message.ShouldContain("ModelRetryMinutes");
    }

    [Test]
    public async Task a_start_with_a_model_retry_of_one_minute_passes()
    {
        // Arrange
        await using var app = BuildWith("1");

        // Act and assert
        Should.NotThrow(() => app.Services.GetRequiredService<IStartupValidator>().Validate());
    }
}
