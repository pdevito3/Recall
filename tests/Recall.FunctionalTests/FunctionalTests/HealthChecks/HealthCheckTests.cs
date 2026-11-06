namespace Recall.FunctionalTests.FunctionalTests.HealthChecks;

using System.Globalization;
using System.Net;
using Serilog.Events;
using Shouldly;
using TestUtilities;

[NotInParallel(nameof(StallingHealthCheck))]
public class HealthCheckTests : TestBase
{
    [Test]
    public async Task health_check_returns_ok()
    {
        // Act
        var result = await FactoryClient.GetRequestAsync(ApiRoutes.Health);

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task alive_check_returns_ok()
    {
        // Act
        var result = await FactoryClient.GetRequestAsync(ApiRoutes.Alive);

        // Assert
        result.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task health_check_that_the_caller_leaves_logs_no_error()
    {
        // Arrange
        var factory = TestingWebApplicationFactory.Instance;
        var started = factory.StallingHealthCheck.ArmNext();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoutes.Health);
        using var leave = new CancellationTokenSource();

        // Act
        var probe = FactoryClient.SendAsync(request, leave.Token);
        await started.WaitAsync(TimeSpan.FromSeconds(30));
        await leave.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(probe);

        // Assert
        var requestLog = await WaitForLogOfTheProbeThatLeftAsync(factory.CapturedLogs);
        factory.CapturedLogs.InTrace(requestLog.TraceId.ShouldNotBeNull())
            .Where(e => e.Level >= LogEventLevel.Error)
            .Select(e => e.RenderMessage(CultureInfo.InvariantCulture))
            .ShouldBeEmpty();
    }

    // The server ends the request after the client leaves, so its request log comes a little later
    private static async Task<LogEvent> WaitForLogOfTheProbeThatLeftAsync(CapturedLogs logs)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var requestLog = logs.All.FirstOrDefault(e =>
                e.Properties.TryGetValue("RequestPath", out var path) && path.ToString() == $"\"/{ApiRoutes.Health}\""
                && e.Properties.TryGetValue("StatusCode", out var status) && status.ToString() == "499");
            if (requestLog is not null)
                return requestLog;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"No request log with status 499 for {ApiRoutes.Health}.");
            await Task.Delay(20);
        }
    }
}
