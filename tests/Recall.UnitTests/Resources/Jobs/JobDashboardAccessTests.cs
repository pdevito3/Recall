namespace Recall.UnitTests.Resources.Jobs;

using BackWave.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Recall.Server.Domain.Users;
using Recall.Server.Resources.Entrypoints.Jobs;
using Shouldly;

/// <summary>
/// A request to the dashboard passes <c>AuthorizeView</c> first, and that hides each other callback in a request. So
/// these tests call each callback directly.
/// </summary>
public class JobDashboardAccessTests
{
    private sealed class StubOwnerGuard(string? owner) : IOwnerGuard
    {
        public Task<string?> CallerIfOwnerAsync(CancellationToken cancellationToken) => Task.FromResult(owner);

        public Task<string?> JobToolsCallerIfOwnerAsync(CancellationToken cancellationToken) => Task.FromResult(owner);
    }

    private static DefaultHttpContext ContextOf(string? owner) => new()
    {
        RequestServices = new ServiceCollection().AddSingleton<IOwnerGuard>(new StubOwnerGuard(owner)).BuildServiceProvider(),
    };

    private static IEnumerable<Func<HttpContext, ValueTask<bool>>> WriteActionsOf(BackWaveDashboardOptions options) =>
    [
        options.AuthorizeRequeue,
        options.AuthorizeCancel,
        options.AuthorizePauseQueue,
        options.AuthorizeTriggerSchedule,
    ];

    private static async Task<List<bool>> AnswersAsync(BackWaveDashboardOptions options, string? owner)
    {
        var answers = new List<bool>();
        foreach (var action in WriteActionsOf(options))
            answers.Add(await action(ContextOf(owner)));
        return answers;
    }

    /// <summary>A functional test cannot stop a host: Program.cs closes the static logger at the stop, and that silences the other tests of the run.</summary>
    [Test]
    public async Task a_stop_of_the_host_ends_a_live_stream_at_once()
    {
        // Arrange
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        using var stopping = new CancellationTokenSource();
        lifetime.ApplicationStopping.Returns(stopping.Token);
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(lifetime).BuildServiceProvider(),
        };
        context.Request.QueryString = new QueryString("?live=1");
        var ended = new TaskCompletionSource();
        var stream = JobDashboard.LimitLiveStreamAndExplainRefusalAsync(context, streaming =>
        {
            streaming.RequestAborted.Register(ended.SetResult);
            return ended.Task;
        });

        // Act
        await stopping.CancelAsync();
        await stream.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        ended.Task.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Test]
    public async Task an_action_with_no_callback_is_denied_for_the_owner_too()
    {
        // Act
        var answers = await AnswersAsync(new BackWaveDashboardOptions(), "the-owner");

        // Assert
        answers.ShouldAllBe(allowed => !allowed);
    }

    [Test]
    public async Task each_action_of_the_dashboard_is_denied_for_a_caller_who_is_not_the_owner()
    {
        // Act
        var answers = await AnswersAsync(JobDashboard.DashboardOptions, owner: null);

        // Assert
        answers.ShouldAllBe(allowed => !allowed);
    }

    [Test]
    public async Task each_action_of_the_dashboard_is_allowed_for_the_owner()
    {
        // Act
        var answers = await AnswersAsync(JobDashboard.DashboardOptions, "the-owner");

        // Assert
        answers.ShouldAllBe(allowed => allowed);
    }
}
