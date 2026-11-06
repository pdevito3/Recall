namespace Recall.UnitTests.Resources.Jobs;

using BackWave.Dashboard;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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

    [Test]
    public async Task an_action_with_no_callback_is_denied_for_the_owner_too()
    {
        foreach (var action in WriteActionsOf(new BackWaveDashboardOptions()))
        {
            // Act
            var allowed = await action(ContextOf("the-owner"));

            // Assert
            allowed.ShouldBeFalse();
        }
    }

    [Test]
    public async Task each_action_of_the_dashboard_is_denied_for_a_caller_who_is_not_the_owner()
    {
        foreach (var action in WriteActionsOf(JobDashboard.DashboardOptions))
        {
            // Act
            var allowed = await action(ContextOf(owner: null));

            // Assert
            allowed.ShouldBeFalse();
        }
    }

    [Test]
    public async Task each_action_of_the_dashboard_is_allowed_for_the_owner()
    {
        foreach (var action in WriteActionsOf(JobDashboard.DashboardOptions))
        {
            // Act
            var allowed = await action(ContextOf("the-owner"));

            // Assert
            allowed.ShouldBeTrue();
        }
    }
}
