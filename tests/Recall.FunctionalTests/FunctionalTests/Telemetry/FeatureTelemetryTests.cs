namespace Recall.FunctionalTests.FunctionalTests.Telemetry;

using System.Diagnostics;
using System.Net;
using Recall.Server.Domain.Agents.Features;
using Recall.Server.Domain.Tenants.Features;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.EntityGraphs;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using Serilog.Events;
using Shouldly;
using TestUtilities;
using Telemetry = Recall.Server.Resources.Telemetry;

[NotInParallel]
public class FeatureTelemetryTests : TestBase
{
    private const string RoleAddedTemplate = "Added the role {Role} to user {UserId}, ending {ClearedOverrides} overrides";
    private const string RoleRemovedTemplate = "Removed the role {Role} from user {UserId}";

    [Test]
    public async Task a_role_add_tags_the_user_and_the_role_and_logs_the_add()
    {
        // Arrange
        var (admin, member) = await AddAdminAndMemberAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);

        // Act
        var result = await FactoryClient.PostJsonRequestAsync(ApiRoutes.Users.AddRole(member.Id), new { role = "Admin" });

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        var slice = SpanOf("AddUserRole.Command", admin);
        slice.GetTagItem("app.user_id").ShouldBe(member.Id);
        slice.GetTagItem("app.user.role").ShouldBe("Admin");

        var log = TestingWebApplicationFactory.Instance.CapturedLogs.InTrace(slice.TraceId)
            .Where(e => e.MessageTemplate.Text == RoleAddedTemplate)
            .ShouldHaveSingleItem();
        Scalar(log, "UserId").ShouldBe(member.Id);
        Scalar(log, "Role").ShouldBe("Admin");
        Scalar(log, "ClearedOverrides").ShouldBe(0);
        Scalar(log, "CallSite").ShouldBeOfType<string>().ShouldStartWith("AddUserRole.cs:Handle@");
    }

    [Test]
    public async Task a_role_removal_tags_the_user_and_the_role_and_logs_the_removal()
    {
        // Arrange
        var (admin, member) = await AddAdminAndMemberAsync(["Auditor"]);
        FactoryClient.SignInAsAppUser(admin.Identifier);

        // Act
        var result = await FactoryClient.DeleteRequestAsync(ApiRoutes.Users.RemoveRole(member.Id, "Auditor"));

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        var slice = SpanOf("RemoveUserRole.Command", admin);
        slice.GetTagItem("app.user_id").ShouldBe(member.Id);
        slice.GetTagItem("app.user.role").ShouldBe("Auditor");

        var log = TestingWebApplicationFactory.Instance.CapturedLogs.InTrace(slice.TraceId)
            .Where(e => e.MessageTemplate.Text == RoleRemovedTemplate)
            .ShouldHaveSingleItem();
        Scalar(log, "UserId").ShouldBe(member.Id);
        Scalar(log, "Role").ShouldBe("Auditor");
        Scalar(log, "CallSite").ShouldBeOfType<string>().ShouldStartWith("RemoveUserRole.cs:Handle@");
    }

    [Test]
    public async Task a_forbidden_role_removal_has_the_user_on_its_span_and_logs_no_change()
    {
        // Arrange
        var (admin, member) = await AddAdminAndMemberAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);

        // Act
        var result = await FactoryClient.DeleteRequestAsync(ApiRoutes.Users.RemoveRole(admin.Id, "Admin"));

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var slice = SpanOf("RemoveUserRole.Command", member);
        slice.GetTagItem("app.user_id").ShouldBe(admin.Id);
        TestingWebApplicationFactory.Instance.CapturedLogs.InTrace(slice.TraceId)
            .ShouldNotContain(e => e.MessageTemplate.Text == RoleRemovedTemplate);
    }

    [Test]
    public async Task a_forbidden_get_user_has_the_user_on_its_span()
    {
        // Arrange
        var (admin, member) = await AddAdminAndMemberAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);

        // Act
        var result = await FactoryClient.GetRequestAsync(ApiRoutes.Users.GetRecord(admin.Id));

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        SpanOf("GetUser.Query", member).GetTagItem("app.user_id").ShouldBe(admin.Id);
    }

    [Test]
    public async Task a_forbidden_get_agent_has_the_agent_on_its_span()
    {
        // Arrange
        await StartGraph()
            .AddTenant(out var tenant)
            .AddUser(out var member, u => u.Roles = [])
            .Under(tenant)
            .AddAgent(out var agent)
            .BuildAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);

        // Act
        var result = await FactoryClient.GetRequestAsync(ApiRoutes.Agents.GetRecord(agent.Id));

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        SpanOf("GetAgent.Query", member).GetTagItem("app.agent_id").ShouldBe(agent.Id);
    }

    [Test]
    public async Task a_role_add_and_a_role_removal_count_on_the_role_changed_metric_with_the_role_and_the_change()
    {
        // Arrange
        var (admin, member) = await AddAdminAndMemberAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var addedBefore = RoleChangedTotal("Auditor", "added");
        var removedBefore = RoleChangedTotal("Auditor", "removed");

        // Act
        var added = await FactoryClient.PostJsonRequestAsync(ApiRoutes.Users.AddRole(member.Id), new { role = "Auditor" });
        var addedAfter = RoleChangedTotal("Auditor", "added");
        var removed = await FactoryClient.DeleteRequestAsync(ApiRoutes.Users.RemoveRole(member.Id, "Auditor"));

        // Assert
        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        addedAfter.ShouldBeGreaterThan(addedBefore);
        RoleChangedTotal("Auditor", "removed").ShouldBeGreaterThan(removedBefore);
    }

    [Test]
    public async Task an_add_of_a_held_role_with_no_override_logs_no_change_and_counts_no_change()
    {
        // Arrange
        var (admin, member) = await AddAdminAndMemberAsync(["UserManager"]);
        FactoryClient.SignInAsAppUser(admin.Identifier);
        var before = RoleChangedTotal("UserManager", "added");

        // Act
        var result = await FactoryClient.PostJsonRequestAsync(ApiRoutes.Users.AddRole(member.Id), new { role = "UserManager" });

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        var slice = SpanOf("AddUserRole.Command", admin);
        TestingWebApplicationFactory.Instance.CapturedLogs.InTrace(slice.TraceId)
            .ShouldNotContain(e => e.MessageTemplate.Text == RoleAddedTemplate);
        RoleChangedTotal("UserManager", "added").ShouldBe(before);
    }

    [Test]
    public async Task a_role_add_that_the_database_does_not_save_logs_no_change()
    {
        // Arrange
        var (admin, member) = await AddAdminAndMemberAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);
        TestingWebApplicationFactory.Instance.FailingSaves.Arm(member.Id);

        // Act
        var result = await FactoryClient.PostJsonRequestAsync(ApiRoutes.Users.AddRole(member.Id), new { role = "Admin" });

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var slice = SpanOf("AddUserRole.Command", admin);
        TestingWebApplicationFactory.Instance.CapturedLogs.InTrace(slice.TraceId)
            .ShouldNotContain(e => e.MessageTemplate.Text == RoleAddedTemplate);
    }

    [Test]
    public async Task get_agent_tags_the_agent()
    {
        // Arrange
        await StartGraph()
            .AddTenant(out _)
            .AddAgent(out var agent)
            .BuildAsync();
        using var parent = Telemetry.Source.StartActivity("test.parent").ShouldNotBeNull();

        // Act
        await SendAsync(new GetAgent.Query(agent.Id));
        parent.Stop();

        // Assert
        var slice = ExportedSpans()
            .Where(a => a.TraceId == parent.TraceId && a.DisplayName == "GetAgent.Query")
            .ShouldHaveSingleItem();
        slice.GetTagItem("app.agent_id").ShouldBe(agent.Id);
    }

    [Test]
    public async Task delete_tenant_tags_the_tenant()
    {
        // Arrange
        await StartGraph()
            .AddTenant(out var tenant)
            .BuildAsync();
        using var parent = Telemetry.Source.StartActivity("test.parent").ShouldNotBeNull();

        // Act
        await ExecuteScopeAsync(sp => sp.GetRequiredService<ISender>().Send(new DeleteTenant.Command(tenant.Id)));
        parent.Stop();

        // Assert
        var slice = ExportedSpans()
            .Where(a => a.TraceId == parent.TraceId && a.DisplayName == "DeleteTenant.Command")
            .ShouldHaveSingleItem();
        slice.GetTagItem("app.tenant_id").ShouldBe(tenant.Id);
    }

    [Test]
    public async Task an_authenticated_request_writes_no_cache_log()
    {
        // Arrange
        var (admin, _) = await AddAdminAndMemberAsync();
        FactoryClient.SignInAsAppUser(admin.Identifier);

        // Act
        var result = await FactoryClient.GetRequestAsync(ApiRoutes.Users.GetRecord(admin.Id));

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
        var slice = SpanOf("GetUser.Query", admin);
        TestingWebApplicationFactory.Instance.CapturedLogs.InTrace(slice.TraceId)
            .Where(e => e.Properties.TryGetValue("SourceContext", out var source)
                && source.ToString().Contains("ZiggyCreatures.Caching.Fusion", StringComparison.Ordinal))
            .Select(e => e.MessageTemplate.Text)
            .ShouldBeEmpty();
    }

    private static async Task<(User Admin, User Member)> AddAdminAndMemberAsync(IReadOnlyList<string>? memberRoles = null)
    {
        await StartGraph()
            .AddTenant(out _)
            .AddUsers(out var users, u => u.Roles = ["Admin"], u => u.Roles = memberRoles ?? [])
            .BuildAsync();
        return (users[0], users[1]);
    }

    private Activity SpanOf(string feature, User caller) => ExportedSpans()
        .Where(a => a.DisplayName == feature && a.GetTagItem("app.caller.subject") as string == caller.Identifier)
        .ShouldHaveSingleItem();

    private static object? Scalar(LogEvent logEvent, string property)
        => logEvent.Properties[property].ShouldBeOfType<ScalarValue>().Value;

    // The exporter is cumulative, so the last point for the role and the change holds the total since the app started
    private static long RoleChangedTotal(string role, string change)
    {
        var factory = TestingWebApplicationFactory.Instance;
        factory.Services.GetRequiredService<MeterProvider>().ForceFlush().ShouldBeTrue();

        return factory.ExportedMetrics.Snapshot()
            .Where(m => m.Name == "app.users.role_changed" && m.MeterName == "Recall.Server")
            .SelectMany(m => m.MetricPoints)
            .Where(p => HasTag(p, "app.user.role", role) && HasTag(p, "app.user.role_change", change))
            .Select(p => p.GetSumLong())
            .DefaultIfEmpty(0)
            .Last();
    }

    private static bool HasTag(MetricPoint point, string key, object value)
    {
        foreach (var tag in point.Tags)
        {
            if (tag.Key == key && Equals(tag.Value, value))
                return true;
        }
        return false;
    }
}
