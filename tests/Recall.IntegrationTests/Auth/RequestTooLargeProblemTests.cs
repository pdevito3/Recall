namespace Recall.IntegrationTests.Auth;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Recall.Server.Resources.Entrypoints.Http;
using Shouldly;

public class RequestTooLargeProblemTests
{
    [Test]
    [Arguments("/throws")]
    [Arguments("/empty")]
    public async Task a_413_gets_the_problem_body_with_the_text_for_a_person(string path)
    {
        // Arrange
        await using var app = await StartAsync();

        // Act
        using var response = await app.GetTestClient().GetAsync(path);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        problem!.Detail.ShouldBe(RequestTooLargeProblem.Detail);
    }

    [Test]
    public async Task another_answer_passes_through()
    {
        // Arrange
        await using var app = await StartAsync();

        // Act
        using var response = await app.GetTestClient().GetAsync("/ok");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("fine");
    }

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddProblemDetails();

        var app = builder.Build();
        app.UseRequestTooLargeProblem();
        app.MapGet("/throws", void () => throw new BadHttpRequestException("Too large.", StatusCodes.Status413PayloadTooLarge));
        app.MapGet("/empty", () => Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        app.MapGet("/ok", () => "fine");
        await app.StartAsync();
        return app;
    }
}
